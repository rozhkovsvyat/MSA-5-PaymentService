using Npgsql;
using NpgsqlTypes;

namespace PaymentService.Payments;

/// <summary>Возвраты постфактум в PostgreSQL.</summary>
public sealed class RefundStore(NpgsqlDataSource db)
{
    /// <returns><c>false</c>, если возврат по платежу уже есть.</returns>
    public async Task<bool> InsertAsync(string paymentId, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("""
            insert into refund (payment_id, status) values (@payment, @status) on conflict (payment_id) do nothing
            """);
        cmd.Parameters.AddWithValue("payment", paymentId);
        cmd.Parameters.AddWithValue("status", RefundStatus.Requested);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    /// <returns>Экземпляр процесса возврата, за которым закреплён возврат: первый записанный.</returns>
    public async Task<long?> SetProcessKeyAsync(string paymentId, long processInstanceKey, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("""
            update refund set process_instance_key = coalesce(process_instance_key, @process) where payment_id = @payment
            returning process_instance_key
            """);
        cmd.Parameters.AddWithValue("payment", paymentId);
        cmd.Parameters.AddWithValue("process", processInstanceKey);
        return await cmd.ExecuteScalarAsync(ct) is long key ? key : null;
    }

    public async Task<Refund?> GetAsync(string paymentId, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("""
            select payment_id, status, attempt, refund_deadline, recovery_status, recovered_amount, debt_amount
            from refund where payment_id = @payment
            """);
        cmd.Parameters.AddWithValue("payment", paymentId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new Refund(r.GetString(0), r.GetString(1), r.GetInt32(2),
            r.IsDBNull(3) ? null : r.GetFieldValue<DateTimeOffset>(3), r.IsDBNull(4) ? null : r.GetString(4),
            r.IsDBNull(5) ? null : r.GetDecimal(5), r.IsDBNull(6) ? null : r.GetDecimal(6));
    }

    /// <summary>Переход возврата, только если он разрешён из текущего состояния.</summary>
    /// <param name="refundDeadline">Срок исполнения отправленного возврата.</param>
    /// <param name="clearDeadline">Возврат точно не выполнен: следующая отправка с тем же ключом получит новый срок.</param>
    public async Task<bool> TransitionAsync(string paymentId, string status, DateTimeOffset? refundDeadline = null, int? attempt = null,
        bool clearDeadline = false, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await TransitionAsync(conn, null, paymentId, status, refundDeadline, attempt, clearDeadline, ct);
    }

    /// <summary>Банк отправку не исполнил: возврат снова ждёт отправки с тем же ключом.</summary>
    public async Task MarkNotExecutedAsync(string paymentId, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("""
            update refund set status = @requested, refund_deadline = null, unexecuted_sends = unexecuted_sends + 1, updated_at = now()
            where payment_id = @payment and status = @unknown
            """);
        cmd.Parameters.AddWithValue("payment", paymentId);
        cmd.Parameters.AddWithValue("requested", RefundStatus.Requested);
        cmd.Parameters.AddWithValue("unknown", RefundStatus.StatusUnknown);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <returns><c>true</c>, если неисполненных отправок набралось <paramref name="limit"/>; счёт при этом начинается заново.</returns>
    public async Task<bool> TakeUnexecutedLimitAsync(string paymentId, int limit, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(
            "update refund set unexecuted_sends = 0 where payment_id = @payment and unexecuted_sends >= @limit");
        cmd.Parameters.AddWithValue("payment", paymentId);
        cmd.Parameters.AddWithValue("limit", limit);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    /// <summary>Запись в журнал и переход возврата одной транзакцией.</summary>
    public async Task RecordAsync(string idempotencyKey, string paymentId, string step, string result, string? status, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await PaymentStore.InsertOperationAsync(conn, tx, idempotencyKey, paymentId, step, result, ct);
        if (status is not null) await TransitionAsync(conn, tx, paymentId, status, null, null, false, ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>Итог удержания с продавца и запись в журнал одной транзакцией.</summary>
    public async Task RecordRecoveryAsync(string idempotencyKey, string paymentId, RecoveryResult result, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await PaymentStore.InsertOperationAsync(conn, tx, idempotencyKey, paymentId, "RECOVER_FROM_COUNTERPARTY",
            $"Recovered {result.Recovered:0.00}, debt {result.Debt:0.00}", ct);
        await using (var cmd = new NpgsqlCommand("""
            update refund set recovery_status = @status, recovered_amount = @recovered, debt_amount = @debt, updated_at = now()
            where payment_id = @payment and recovery_status = @pending
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("payment", paymentId);
            cmd.Parameters.AddWithValue("status", result.Debt > 0 ? RecoveryStatus.DebtRecorded : RecoveryStatus.Recovered);
            cmd.Parameters.AddWithValue("recovered", result.Recovered);
            cmd.Parameters.AddWithValue("debt", result.Debt);
            cmd.Parameters.AddWithValue("pending", RecoveryStatus.Pending);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    private static async Task<bool> TransitionAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string paymentId, string status,
        DateTimeOffset? refundDeadline, int? attempt, bool clearDeadline, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            update refund set
                status = @status,
                refund_deadline = case when @clear then null else coalesce(@deadline, refund_deadline) end,
                attempt = coalesce(@attempt, attempt),
                unexecuted_sends = case when @attempt is null then unexecuted_sends else 0 end,
                recovery_status = case when @status = 'REFUNDED' then coalesce(recovery_status, 'RECOVERY_PENDING') else recovery_status end,
                updated_at = now()
            where payment_id = @payment and status = any(@from)
            """, conn, tx);
        cmd.Parameters.AddWithValue("payment", paymentId);
        cmd.Parameters.AddWithValue("status", status);
        cmd.Parameters.AddWithValue("from", RefundStatus.AllowedFrom(status));
        cmd.Parameters.Add(new NpgsqlParameter("deadline", NpgsqlDbType.TimestampTz) { Value = (object?)refundDeadline ?? DBNull.Value });
        cmd.Parameters.AddWithValue("clear", clearDeadline);
        cmd.Parameters.Add(new NpgsqlParameter("attempt", NpgsqlDbType.Integer) { Value = (object?)attempt ?? DBNull.Value });
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }
}

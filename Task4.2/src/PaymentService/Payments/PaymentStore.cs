using Npgsql;
using NpgsqlTypes;

namespace PaymentService.Payments;

/// <summary>Платежи, журнал денежных операций и запросы маркетплейса в PostgreSQL.</summary>
public sealed class PaymentStore(NpgsqlDataSource db)
{
    public async Task<bool> InsertAsync(NewPayment p, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("""
            insert into payment (id, client_id, counterparty_id, amount, currency, status, accepted_at)
            values (@id, @client, @counterparty, @amount, @currency, @status, @accepted)
            on conflict (id) do nothing
            """);
        cmd.Parameters.AddWithValue("id", p.PaymentId);
        cmd.Parameters.AddWithValue("client", p.ClientId);
        cmd.Parameters.AddWithValue("counterparty", p.CounterpartyId);
        cmd.Parameters.AddWithValue("amount", p.Amount);
        cmd.Parameters.AddWithValue("currency", p.Currency);
        cmd.Parameters.AddWithValue("status", PaymentStatus.Created);
        cmd.Parameters.AddWithValue("accepted", p.AcceptedAt);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<Payment?> GetAsync(string id, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("""
            select id, client_id, counterparty_id, amount, currency, status, cut_off, reject_reason, transfer_deadline,
                   accepted_at, finished_at
            from payment where id = @id
            """);
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new Payment(r.GetString(0), r.GetString(1), r.GetString(2), r.GetDecimal(3), r.GetString(4), r.GetString(5),
            r.GetBoolean(6), r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? null : r.GetFieldValue<DateTimeOffset>(8),
            r.GetFieldValue<DateTimeOffset>(9), r.IsDBNull(10) ? null : r.GetFieldValue<DateTimeOffset>(10));
    }

    /// <summary>Переход в <paramref name="status"/>, только если он разрешён из текущего состояния.</summary>
    /// <returns><c>false</c>, если переход не разрешён: платёж уже дальше или в конечном состоянии.</returns>
    public async Task<bool> TransitionAsync(string id, string status, bool? cutOff = null, string? rejectReason = null,
        DateTimeOffset? transferDeadline = null, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await TransitionAsync(conn, null, id, status, cutOff, rejectReason, transferDeadline, ct);
    }

    public async Task<string?> FindOperationAsync(string idempotencyKey, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("select result from payment_operation where idempotency_key = @key");
        cmd.Parameters.AddWithValue("key", idempotencyKey);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    /// <summary>Запись в журнал и переход состояния одной транзакцией.</summary>
    public async Task RecordAsync(string idempotencyKey, string paymentId, string step, string result, string? status, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await InsertOperationAsync(conn, tx, idempotencyKey, paymentId, step, result, ct);
        if (status is not null) await TransitionAsync(conn, tx, paymentId, status, null, null, null, ct);
        await tx.CommitAsync(ct);
    }

    internal static async Task InsertOperationAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string idempotencyKey, string paymentId,
        string step, string result, CancellationToken ct)
    {
        await using var insert = new NpgsqlCommand("""
            insert into payment_operation (idempotency_key, payment_id, step, result)
            values (@key, @payment, @step, @result)
            on conflict (idempotency_key) do nothing
            """, conn, tx);
        insert.Parameters.AddWithValue("key", idempotencyKey);
        insert.Parameters.AddWithValue("payment", paymentId);
        insert.Parameters.AddWithValue("step", step);
        insert.Parameters.AddWithValue("result", result);
        await insert.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<PaymentOperation>> ListOperationsAsync(string paymentId, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(
            "select step, result, created_at from payment_operation where payment_id = @payment order by id");
        cmd.Parameters.AddWithValue("payment", paymentId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<PaymentOperation>();
        while (await r.ReadAsync(ct))
            list.Add(new PaymentOperation(r.GetString(0), r.GetString(1), r.GetFieldValue<DateTimeOffset>(2)));
        return list;
    }

    public async Task<PaymentRequestRecord> RegisterRequestAsync(string requestKey, string requestHash, string newPaymentId, CancellationToken ct)
    {
        await using var insert = db.CreateCommand("""
            insert into payment_request (request_key, request_hash, payment_id) values (@key, @hash, @payment)
            on conflict (request_key) do nothing
            """);
        insert.Parameters.AddWithValue("key", requestKey);
        insert.Parameters.AddWithValue("hash", requestHash);
        insert.Parameters.AddWithValue("payment", newPaymentId);
        if (await insert.ExecuteNonQueryAsync(ct) == 1) return new PaymentRequestRecord(newPaymentId, null, Created: true, Conflict: false);

        await using var select = db.CreateCommand(
            "select payment_id, process_instance_key, request_hash from payment_request where request_key = @key");
        select.Parameters.AddWithValue("key", requestKey);
        await using var r = await select.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return new PaymentRequestRecord(r.GetString(0), r.IsDBNull(1) ? null : r.GetInt64(1), Created: false,
            Conflict: r.GetString(2) != requestHash);
    }

    /// <returns>Экземпляр, за которым закреплён платёж: первый записанный, а не обязательно этот.</returns>
    public async Task<long?> SetRequestProcessAsync(string paymentId, long processInstanceKey, CancellationToken ct)
    {
        await using (var update = db.CreateCommand(
            "update payment_request set process_instance_key = @process where payment_id = @payment and process_instance_key is null"))
        {
            update.Parameters.AddWithValue("payment", paymentId);
            update.Parameters.AddWithValue("process", processInstanceKey);
            await update.ExecuteNonQueryAsync(ct);
        }
        return await FindProcessKeyAsync(paymentId, ct);
    }

    public async Task<long?> FindProcessKeyAsync(string paymentId, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("select process_instance_key from payment_request where payment_id = @payment");
        cmd.Parameters.AddWithValue("payment", paymentId);
        return await cmd.ExecuteScalarAsync(ct) is long key ? key : null;
    }

    private static async Task<bool> TransitionAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string id, string status,
        bool? cutOff, string? rejectReason, DateTimeOffset? transferDeadline, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            update payment set
                status = @status,
                cut_off = coalesce(@cut_off, cut_off),
                reject_reason = coalesce(@reason, reject_reason),
                transfer_deadline = coalesce(@deadline, transfer_deadline),
                finished_at = case when @status in ('COMPLETED', 'REJECTED') then now() else finished_at end,
                updated_at = now()
            where id = @id and status = any(@from)
            """, conn, tx);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("status", status);
        cmd.Parameters.AddWithValue("from", PaymentStatus.AllowedFrom(status));
        cmd.Parameters.Add(new NpgsqlParameter("cut_off", NpgsqlDbType.Boolean) { Value = (object?)cutOff ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("reason", NpgsqlDbType.Text) { Value = (object?)rejectReason ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("deadline", NpgsqlDbType.TimestampTz) { Value = (object?)transferDeadline ?? DBNull.Value });
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }
}

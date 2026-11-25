FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY nuget.config ./
COPY src/PaymentService/PaymentService.csproj src/PaymentService/
RUN dotnet restore src/PaymentService/PaymentService.csproj
COPY src/PaymentService/ src/PaymentService/
RUN dotnet publish src/PaymentService/PaymentService.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
COPY process.bpmn post-factum-refund.bpmn ./models/
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "PaymentService.dll"]

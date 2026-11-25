FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY nuget.config ./
COPY src/FraudCheckService/FraudCheckService.csproj src/FraudCheckService/
RUN dotnet restore src/FraudCheckService/FraudCheckService.csproj
COPY src/FraudCheckService/ src/FraudCheckService/
RUN dotnet publish src/FraudCheckService/FraudCheckService.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "FraudCheckService.dll"]

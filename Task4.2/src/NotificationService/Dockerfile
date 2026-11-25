FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY nuget.config ./
COPY src/NotificationService/NotificationService.csproj src/NotificationService/
RUN dotnet restore src/NotificationService/NotificationService.csproj
COPY src/NotificationService/ src/NotificationService/
RUN dotnet publish src/NotificationService/NotificationService.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "NotificationService.dll"]

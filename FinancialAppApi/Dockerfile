# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["FinancialAppApi.csproj", "./"]
RUN dotnet restore "FinancialAppApi.csproj"
COPY . .
RUN dotnet publish "FinancialAppApi.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
# Create the /data directory for SQLite volume storage
RUN mkdir -p /data
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "FinancialAppApi.dll"]

# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["FinancialAppApi.csproj", "./"]
# Restore for the target runtime so ReadyToRun (crossgen) can precompile for linux-x64.
RUN dotnet restore "FinancialAppApi.csproj" -r linux-x64
COPY . .
# ReadyToRun ahead-of-time compilation cuts JIT work on the cold-start path.
# --self-contained false keeps the image small (framework lives in the runtime image).
RUN dotnet publish "FinancialAppApi.csproj" \
    -c Release \
    -r linux-x64 \
    --self-contained false \
    -p:PublishReadyToRun=true \
    -p:UseAppHost=false \
    -o /app/publish

# Runtime stage -- chiseled image: smaller and faster to pull/provision than the full
# aspnet:10.0 image, which shortens the time before a scaled-from-zero instance is ready.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS final
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
# Workstation GC starts faster and uses less memory than Server GC, which fits a
# single-vCPU free-tier container better than Server GC's per-core heap pre-allocation.
ENV DOTNET_gcServer=0
ENTRYPOINT ["dotnet", "FinancialAppApi.dll"]

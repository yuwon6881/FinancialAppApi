# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# The build context is the backend solution dir (see cloudbuild.yaml `-f ... .`). Copy the
# Central Package Management + shared build props FIRST: they hold every <PackageVersion>,
# so without them the version-less <PackageReference>s in the csproj fail restore with NU1015.
COPY ["Directory.Packages.props", "Directory.Build.props", "./"]
COPY ["FinancialAppApi/FinancialAppApi.csproj", "FinancialAppApi/"]
# Restore for the target runtime so ReadyToRun (crossgen) can precompile for linux-x64.
RUN dotnet restore "FinancialAppApi/FinancialAppApi.csproj" -r linux-x64
COPY . .
# ReadyToRun ahead-of-time compilation cuts JIT work on the cold-start path.
# --self-contained false keeps the image small (framework lives in the runtime image).
RUN dotnet publish "FinancialAppApi/FinancialAppApi.csproj" \
    -c Release \
    -r linux-x64 \
    --self-contained false \
    -p:PublishReadyToRun=true \
    -p:UseAppHost=false \
    -o /app/publish

# Runtime stage. Chiseled: no shell, no package manager, non-root by default -- a
# smaller image (faster Cloud Run cold-pull) with a much smaller CVE surface. This is
# viable because the migrate path no longer needs a shell: the cloudbuild.yaml migrate
# step runs `dotnet FinancialAppApi.dll --migrate` (the session-mode port swap moved
# into Program.cs), and no Kerberos/GSS is used, so the old libgssapi-krb5-2 apt install
# is gone. The chiseled base already ships ICU + tzdata, which InvariantGlobalization=false
# requires. If a future dependency needs Kerberos, switch to the `-noble-chiseled-extra` tag.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS final
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
# Workstation GC starts faster and uses less memory than Server GC, which fits a
# single-vCPU free-tier container better than Server GC's per-core heap pre-allocation.
ENV DOTNET_gcServer=0
ENTRYPOINT ["dotnet", "FinancialAppApi.dll"]

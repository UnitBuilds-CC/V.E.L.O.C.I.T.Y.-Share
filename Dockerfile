# Multi-stage Dockerfile for V.E.L.O.C.I.T.Y.-Share Server
# Compiles Rust FFI libraries and publishes ASP.NET Core server on Linux

# Stage 1: Compile Rust FFI (1.85+ required for edition 2024 crates)
FROM rust:1-slim AS rust-builder
RUN apt-get update && apt-get install -y --no-install-recommends cmake build-essential && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY velocity_share_ffi/ .
RUN cargo build --release

# Stage 2: Publish .NET 10.0 Server
FROM mcr.microsoft.com/dotnet/sdk:10.0-preview AS dotnet-builder
WORKDIR /app
COPY VelocityShare.Server/ VelocityShare.Server/
# Place Rust FFI output where the CopyRustSo MSBuild target expects it
COPY --from=rust-builder /app/target/release/libvelocity_share_ffi.so velocity_share_ffi/target/release/
RUN dotnet publish VelocityShare.Server/VelocityShare.Server.csproj -c Release -o out

# Stage 3: Container Runner
FROM mcr.microsoft.com/dotnet/aspnet:10.0-preview AS runtime
WORKDIR /app

# Install curl for health checks (multi-distro: Debian or Azure Linux/Mariner)
RUN if command -v apt-get >/dev/null 2>&1; then \
        apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*; \
    elif command -v tdnf >/dev/null 2>&1; then \
        tdnf install -y curl && tdnf clean all; \
    else \
        echo "curl should already be available"; \
    fi

# Create non-root user for security
RUN groupadd --system --gid 1001 velocityshare && \
    useradd --system --uid 1001 --gid velocityshare --no-create-home velocityshare

COPY --from=dotnet-builder /app/out .
COPY --from=rust-builder /app/target/release/libvelocity_share_ffi.so .

# Configure Linux environment for FFI shared library loading
ENV LD_LIBRARY_PATH=/app
ENV ASPNETCORE_URLS=http://+:5000
EXPOSE 5000

# Health check for container orchestration
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD curl -f http://localhost:5000/health || exit 1

# Switch to non-root user
USER velocityshare

ENTRYPOINT ["dotnet", "VelocityShare.Server.dll"]

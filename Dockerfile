# Multi-stage Dockerfile for V.E.L.O.C.I.T.Y.-Share Server
# Compiles Rust FFI libraries and publishes ASP.NET Core server on Linux

# Stage 1: Compile Rust FFI
FROM rust:1.80 AS rust-builder
WORKDIR /app
COPY velocity_share_ffi/ .
RUN cargo build --release

# Stage 2: Publish .NET 10.0 Server
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS dotnet-builder
WORKDIR /app
COPY VelocityShare.Server/ VelocityShare.Server/
RUN dotnet publish VelocityShare.Server/VelocityShare.Server.csproj -c Release -o out

# Stage 3: Container Runner
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=dotnet-builder /app/out .
COPY --from=rust-builder /app/target/release/libvelocity_share_ffi.so .

# Configure Linux environment for FFI shared library loading
ENV LD_LIBRARY_PATH=/app
ENV ASPNETCORE_URLS=http://+:5000
EXPOSE 5000

ENTRYPOINT ["dotnet", "VelocityShare.Server.dll"]

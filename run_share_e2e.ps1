# V.E.L.O.C.I.T.Y. Share Local Client-to-Client E2E Test Runner
Write-Host "=====================================================================" -ForegroundColor Cyan
Write-Host "          Building and Running VelocityShare True E2E Test" -ForegroundColor Cyan
Write-Host "=====================================================================" -ForegroundColor Cyan

# 1. Build the E2E test project
dotnet build -c Release VelocityShare.E2ETest\VelocityShare.E2ETest.csproj

if ($LASTEXITCODE -ne 0) {
    Write-Error "Build failed!"
    exit 1
}

# 2. Run the E2E test executable
Write-Host "[Running] Starting E2E test client..." -ForegroundColor Yellow
dotnet run --project VelocityShare.E2ETest\VelocityShare.E2ETest.csproj -c Release

if ($LASTEXITCODE -ne 0) {
    Write-Error "E2E test run failed!"
    exit 1
}

Write-Host "=====================================================================" -ForegroundColor Cyan
Write-Host "          VELOCITYSHARE E2E CLIENT-TO-CLIENT TEST RUN COMPLETED!" -ForegroundColor Green
Write-Host "=====================================================================" -ForegroundColor Cyan

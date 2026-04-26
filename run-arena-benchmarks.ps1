# run-arena-benchmarks.ps1
# Runs the Arena collection benchmarks comparing managed vs unmanaged performance.

Write-Host "Building project in Release mode..." -ForegroundColor Cyan
dotnet build -c Release

Write-Host "Running Arena Collection Benchmarks..." -ForegroundColor Cyan
dotnet run -c Release --project src/MongoZen.Benchmarks/MongoZen.Benchmarks.csproj -- --filter *ArenaCollectionBenchmarks*

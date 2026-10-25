# Builds FloatTube.exe as a single, self-contained Windows x64 executable.
# Requires the .NET 8 SDK: https://dotnet.microsoft.com/download
dotnet publish .\FloatTube.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -o .\publish

Write-Host ""
Write-Host "Done -> .\publish\EXoticaFloatube.exe"

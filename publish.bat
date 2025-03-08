
rem If we want to publish a completely self-contained (and enormous) package:
rem dotnet publish -p:PublishSingleFile=true -r win-x64 -f net8.0-windows -c Release --self-contained true ImageViewer\ImageViewer.csproj -o .\dist

dotnet publish -p:PublishSingleFile=true -r win-x64 -f net8.0-windows -c Release --self-contained false ImageViewer\ImageViewer.csproj -o .\dist

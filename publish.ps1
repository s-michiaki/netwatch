# NetWatch をランタイム同梱（.NET のインストール不要）で publish フォルダに出力する
dotnet publish -c Release -r win-x64 --self-contained true -o publish
Write-Host "出力先: $PSScriptRoot\publish\NetWatch.exe"

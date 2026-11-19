# NetWatch を単一ファイル（NetWatch.exe・.NET ランタイム同梱）で publish フォルダに出力する
dotnet publish -c Release -o publish
Write-Host "出力先: $PSScriptRoot\publish\NetWatch.exe"

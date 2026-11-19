配布形式の変更のみのリリースです（機能は v0.4.0 と同じ）。

## 変更点
- zip の中身を **`NetWatch.exe` 1 つ**（＋ライセンス表記 `THIRD-PARTY-NOTICES.txt`）にまとめました。DLL が並んで exe が探しにくい問題を解消しています
- .NET ランタイムは exe に同梱されているので、引き続きインストール不要です

## 補足
- 初回起動時に中身を `%TEMP%\.net\NetWatch\` に展開するため、最初の起動だけ数秒かかります（2 回目以降は速くなります）
- 未署名の実行ファイルなので、SmartScreen の警告が出たら「詳細情報」→「実行」を選んでください
- 国別マップの GeoIP データベースを更新した場合、更新版は `%AppData%\NetWatch` に保存されます

## クレジット
- IP Geolocation by [DB-IP](https://db-ip.com)（CC BY 4.0）
- 地図：Made with [Natural Earth](https://www.naturalearthdata.com)

# StickyMiniWeb (WPF + WebView2)

小さな常時前面ウィンドウで任意の Web ページを表示する Windows アプリのサンプルです。  
**Topmost** トグルで最前面の ON/OFF を切替できます。初期URLは `http://localhost:8000`。

## 使い方
1. Visual Studio 2022 でこのフォルダをプロジェクトとして開くか、`dotnet build` でビルド。
2. 依存: NuGet `Microsoft.Web.WebView2`（csproj で参照済み）。
3. 実行すると小さなウィンドウが開き、テキストボックスに入力した URL を表示します。

## 参考ドキュメント
- Get started with WebView2 (WPF): https://learn.microsoft.com/microsoft-edge/webview2/get-started/wpf
- NuGet: Microsoft.Web.WebView2: https://www.nuget.org/packages/Microsoft.Web.WebView2
- WPF Window.Topmost: https://learn.microsoft.com/dotnet/api/system.windows.window.topmost

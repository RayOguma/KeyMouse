# KeyMouse

キーボードだけでマウスを操作する Windows 常駐アプリ。

## インストール（使うだけの人）

1. [Releases ページ](https://github.com/RayOguma/KeyMouse/releases/latest) を開く
2. `KeyMouse-win-x64.zip` をダウンロードして、好きな場所に展開する
3. `KeyMouse.exe` をダブルクリックで起動 → タスクトレイに常駐します

- .NET のインストールは不要です（必要なものは exe に同梱済み）。
- 初回起動時に「Windows によって PC が保護されました」と出た場合は、「詳細情報」→「実行」を押してください（署名していないアプリのため表示されます）。
- Windows 起動時に自動で立ち上げたい場合は、設定画面の「Windows の起動時に自動で起動する」にチェックを入れてください。
- 終了はトレイアイコンを右クリック →「終了」。
- アンインストールは exe を削除するだけです（設定も消す場合は `%APPDATA%\KeyMouse` フォルダも削除）。

対応 OS: Windows 10 / 11（64bit）

## 初期設定（キーは物理的な位置。QWERTY の刻印で表記）

起動キー **無変換** を押している間だけ有効。

| キー | 動作 |
|---|---|
| W / A / S / D | 上 / 左 / 下 / 右へ移動（押し続けると加速） |
| J | 左クリック（押したまま移動でドラッグ） |
| K | 右クリック |
| L | 中クリック |
| H | ドラッグ固定（押すたびに つかむ／離す） |
| R / F | 上 / 下スクロール |
| Q / E | 左 / 右スクロール |
| 左Shift | 押している間ゆっくり |
| Space | 押している間すばやく |

無変換を単独で押して離したときは、普段どおり無変換キーとして入力されます。

## 設定

トレイアイコンをダブルクリック（または右クリック →「設定...」）。
「変更」を押してから割り当てたいキーを押すと登録されます。
設定ファイル: `%APPDATA%\KeyMouse\settings.json`

## 注意

- 管理者権限で動いているウィンドウの上では反応しません（KeyMouse も管理者として起動すれば反応します）。
- キー配列を変えるソフトと併用していて反応しない場合は、設定画面でキーを押し直して登録してください。

## 開発者向け

### ソースからビルド

[.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) が必要です。

```
git clone https://github.com/RayOguma/KeyMouse.git
cd KeyMouse
dotnet run -c Release
```

配布用の単体 exe を作る場合:

```
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish
```

### リリース手順（GitHub Actions）

`main` への push / Pull Request のたびに Actions がビルドし、Actions の実行結果ページから zip をダウンロードできます。

新しいバージョンを Releases に公開するには、`v` から始まるタグを push します。

```
git tag v1.0.1
git push origin v1.0.1
```

数分後に [Releases](https://github.com/RayOguma/KeyMouse/releases) に `KeyMouse-win-x64.zip` が自動で追加されます。

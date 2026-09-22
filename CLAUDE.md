# Kado の進め方

## 配布の手順

「ビルドして」と言われたら、**タグを打つところまでを1回で出す**。

1. `dev` でリリースワークフローを走らせる（バージョンは前回の次の番号）
2. 成功を確かめる
3. タグを打つコマンドを、そのまま貼り付けられる形で渡す

タグは Claude Code のセッションからは push できない（ブランチは通るが
`refs/tags/*` は 403 が返る）。**必ず手元で打ってもらう。**

ビルドの成果物（Artifacts）と Releases は別物。更新機能は Releases の最新版を
見に行くので、**タグを打つまでアプリは新しい版に気づかない。** 成果物だけ渡して
「更新が来ない」となったことがある。

```powershell
cd $HOME
Remove-Item -Recurse -Force kado-tag -ErrorAction SilentlyContinue
git clone https://github.com/Yu5rin/Kado.git kado-tag
cd kado-tag
git checkout dev

git tag -a vX.Y.Z -m "Kado X.Y.Z"
git push origin vX.Y.Z
```

## 旧い名前（SlideinaCalendar）

アプリもリポジトリも Kado に改めた。ただし次の2つは**旧い名前のまま**で、
変えてはいけない。

1. データの保存先の引っ越し元。`CalendarDatabase.LegacyFolderName` が
   `%LOCALAPPDATA%\SlideinaCalendar` を指している。0.9.15 より前から使って
   いる人の予定・タスク・設定・Google のトークンはそこに入っている。
2. `WorkdayFeed` が書き出す `"app": "inaCalendar"`。配信ファイル（feed.json）の
   形式を旧い道具と共有しているため。

公開ページの置き場所は `Yu5rin/kado-site` に改めた。ここを変えると GitHub Pages の
URL が変わるので、Google のブランディング欄（ホームページ・プライバシーポリシー）と
Search Console の所有確認も一緒に直すこと。詳しくは `site/README.md`。

Google の同意画面に出るアプリ名は Google Cloud のブランディング欄にあり、
こちらのコードとは別に持っている。変えるならそちらで直す。

## 実機で確かめること

WPF は Linux では動かせないので、CI で分かるのはコンパイルが通ることまで。
起動時にだけ壊れる書き方は `docs/README.md` にまとめてある。書く前に読むこと。

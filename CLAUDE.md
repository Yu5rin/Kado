# SlideinaCalendar の進め方

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
Remove-Item -Recurse -Force slideina-tag -ErrorAction SilentlyContinue
git clone https://github.com/Yu5rin/SlideinaCalendar.git slideina-tag
cd slideina-tag
git checkout dev

git tag -a vX.Y.Z -m "SlideinaCalendar X.Y.Z"
git push origin vX.Y.Z
```

## 実機で確かめること

WPF は Linux では動かせないので、CI で分かるのはコンパイルが通ることまで。
起動時にだけ壊れる書き方は `docs/README.md` にまとめてある。書く前に読むこと。

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
git clone https://github.com/Yu5rin/SlideinaCalendar.git kado-tag
cd kado-tag
git checkout dev

git tag -a vX.Y.Z -m "Kado X.Y.Z"
git push origin vX.Y.Z
```

## リポジトリの名前

アプリの名前は Kado だが、**リポジトリはまだ `Yu5rin/SlideinaCalendar`**。
更新の確認先（`api.github.com/repos/Yu5rin/SlideinaCalendar/releases/latest`）、
clone の URL、README のバッジは、この名前のまま据え置いてある。

リポジトリを Kado へ改名したら、それらを書き換えること。GitHub は旧名から
転送してくれるので急がなくてよいが、転送に頼り続けると分かりにくい。

## 実機で確かめること

WPF は Linux では動かせないので、CI で分かるのはコンパイルが通ることまで。
起動時にだけ壊れる書き方は `docs/README.md` にまとめてある。書く前に読むこと。

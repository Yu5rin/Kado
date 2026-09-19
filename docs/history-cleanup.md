# 公開前に履歴から消す手順

**この作業を済ませるまで、リポジトリを公開しないこと。**

## なぜ要るか

テストに置いていた実働日 Excel に、次が埋め込まれていた。

- 作成者・最終更新者の氏名
- 社内ファイルサーバのパス（部署名・担当者名を含む）
- 会社の略称を含む見出し
- 外部リンク先（個人 PC のユーザー名と、デスクトップ上の業務ファイル名）
- 753 日分の実際の稼働日

このファイル自体はすでに消して、同じ構造の合成データに差し替えてある。
**しかし Git は消す前の中身を履歴に持ち続ける。** 最初のコミットから入っていたので、
公開すると過去のコミットを辿るだけで誰でも取り出せる。

消すべきものは**この1ファイルだけ**。ほかに履歴へ入ったものは調べたうえで問題なしと
判断している（旧 inaCalendar の JSON は最初から匿名化済み、モックと公開用ページには
社内情報なし）。

```
tests/SlideinaCalendar.Core.Tests/TestData/実働日ファイル.xlsx
```

## 作業

Windows なら PowerShell、Mac／Linux なら端末で。**5分ほど。**

### 1. git filter-repo を入れる

```
pip install git-filter-repo
```

（`pip` が無ければ Python を入れる。`git filter-branch` でもできるが、遅く、
書き換え漏れが起きやすいので勧めない。）

### 2. 作業用に新しく clone する

**いま使っている作業フォルダでは行わない。** filter-repo は新鮮な clone を前提にしている。

```
cd （作業用の適当な場所）
git clone https://github.com/Yu5rin/SlideinaCalendar.git cleanup
cd cleanup
```

### 3. 履歴から消す

```
git filter-repo --invert-paths --path "tests/SlideinaCalendar.Core.Tests/TestData/実働日ファイル.xlsx"
```

`--invert-paths` は「指定したものだけを除く」という意味。

### 4. 消えたことを確かめる

```
git log --all --oneline -- "tests/SlideinaCalendar.Core.Tests/TestData/実働日ファイル.xlsx"
```

**何も出なければ成功。** 1行でも出たら、パスの綴りを確かめてやり直す。

差し替えた合成版のほうは残っているはず。消しすぎていないことも見ておく。

```
git log --all --oneline -- "tests/SlideinaCalendar.Core.Tests/TestData/実働日サンプル.xlsx"
```

こちらは**1行出れば正しい**。

> 中身に氏名が残っていないかを `grep` で探す、という確認は**できない**。
> xlsx は ZIP で圧縮されているので、そのまま文字列を探しても出てこない。
> ファイルが履歴から消えていれば中身も消えているので、上の確認で足りる。

（この手順は実際に走らせて確かめてある。Excel は 0 件、合成版は 1 件になり、
リポジトリの容量も減った。）

### 5. 押し戻す

filter-repo は安全のためリモートの登録を外す。付け直してから送る。

```
git remote add origin https://github.com/Yu5rin/SlideinaCalendar.git
git push --force --all origin
git push --force --tags origin
```

## 済んだあと

### 手元の古い clone は捨てる

**書き換え前の clone を使い続けると、古い履歴を押し戻してしまう。** いま使っている
作業フォルダは消して、clone し直す。

```
git clone https://github.com/Yu5rin/SlideinaCalendar.git
```

### GitHub に残る分について

force push のあとも、GitHub の内部には古いコミットがしばらく残る。**ただしコミットの
SHA を知っている人しか辿れない。** 公開する前にこの作業を済ませてあれば、外部の人は
その SHA を知りようがないので、実用上は問題ない。

気になるようなら、[GitHub サポート](https://support.github.com/)に
「force push した古いオブジェクトを消してほしい」と依頼できる。

### 変わるもの・変わらないもの

| | |
|---|---|
| コミットの SHA | **すべて変わる**（履歴を書き換えるため） |
| コミットの中身・順番・メッセージ | そのまま |
| PR #6〜#10 | 残る。ただし差分の表示が崩れることがある |
| Issues・Stars・設定 | そのまま |

## 最後に

この作業が済んでから、リポジトリを公開に切り替える。

Settings → General → いちばん下の Danger Zone → Change repository visibility。

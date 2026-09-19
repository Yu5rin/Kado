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

### 手順（PowerShell に、上から順に貼る）

**1行ずつ貼って、エラーが出ないことを確かめながら進める。**

```powershell
# 1. 道具を入れる（入っていれば飛ばされる）
pip install git-filter-repo

# 2. 前に試したものが残っていれば片付ける
cd $HOME
Remove-Item -Recurse -Force cleanup -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force SlideinaCalendar -ErrorAction SilentlyContinue

# 3. まっさらに取ってくる（この中では二度と clone しない）
git clone https://github.com/Yu5rin/SlideinaCalendar.git cleanup
cd cleanup

# 4. 履歴から消す
git filter-repo --invert-paths --path "tests/SlideinaCalendar.Core.Tests/TestData/実働日ファイル.xlsx"

# 5. 消えたか確かめる（何も出なければ成功）
git log --all --oneline -- "tests/SlideinaCalendar.Core.Tests/TestData/実働日ファイル.xlsx"

# 6. 差し替えた合成版は残っているか（1行出れば正しい）
git log --all --oneline -- "tests/SlideinaCalendar.Core.Tests/TestData/実働日サンプル.xlsx"

# 7. 押し戻す
git remote add origin https://github.com/Yu5rin/SlideinaCalendar.git
git push --force --all origin
git push --force --tags origin
```

### つまずきやすいところ

| 出たもの | 意味 | どうするか |
|---|---|---|
| `Refusing to destructively overwrite repo history`<br>`(you have untracked changes)` | clone したフォルダの中に、余計なものが入っている | 手順2からやり直す。**`cleanup` の中で clone しない** |
| `error: remote origin already exists` | 手順4が走っていない（走ると origin が外れる） | 手順5で消えていないはず。手順2からやり直す |
| `Everything up-to-date` | 書き換わっていないので送るものが無い | 同上。手順4が成功していない |
| 手順5で行が出る | まだ履歴に残っている | パスの綴りを確かめる。日本語のファイル名なので、**引用符ごとコピーする** |

**手順4がいちばんの要。** ここが通らないまま7まで進んでも、リモートは何も変わらない。
手順4を実行したあとは、すべてのコミットの ID が変わる。`git log --oneline` で見て、
`334f6e4` のような見覚えのある ID が消えていれば、書き換わっている。

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

# Google 連携の下ごしらえ

同期を使うには、**ご自身の Google Cloud プロジェクト**でクライアント ID を発行する。
アプリに同梱はしない（同梱すると全利用者で API の割り当てを共有することになる）。

作業は一度だけ。10分ほど。

## リンク一覧（先に開いておくと早い）

| やること | URL |
|---|---|
| 1. プロジェクトを作る | https://console.cloud.google.com/projectcreate |
| 2. Calendar API を有効にする | https://console.cloud.google.com/apis/library/calendar-json.googleapis.com |
| 2. Tasks API を有効にする | https://console.cloud.google.com/apis/library/tasks.googleapis.com |
| 3. 同意画面（ブランディング） | https://console.cloud.google.com/auth/branding |
| 3. 公開ステータス（対象） | https://console.cloud.google.com/auth/audience |
| 4. クライアント ID を発行する | https://console.cloud.google.com/auth/clients |

2 以降は**プロジェクトを選んだ状態で**開くこと。違うプロジェクトで有効化しても効かない。
URL の末尾に `?project=プロジェクトID` を付けると確実。

---

## 1. プロジェクトを作る

https://console.cloud.google.com/projectcreate

名前は何でもよい（例：`slideina-calendar`）。作ったら、以降の画面で**そのプロジェクトが
選ばれていること**を上部の選択欄で確かめる。

## 2. API を有効にする

2つとも「有効にする」を押す。

- Google Calendar API … https://console.cloud.google.com/apis/library/calendar-json.googleapis.com
- Google Tasks API … https://console.cloud.google.com/apis/library/tasks.googleapis.com

参考：[Google Workspace API を有効にする](https://developers.google.com/workspace/guides/enable-apis)

## 3. OAuth 同意画面を作る

https://console.cloud.google.com/auth/branding

- User Type は **外部**（Workspace アカウントなら「内部」でもよい）
- アプリ名・サポートメール・デベロッパーの連絡先を埋める

### ⚠ 公開ステータスは「本番」にする

**「テスト」のままだと、更新トークンが7日で失効する。** 毎週つなぎ直すことになる。
ご自身しか使わなくても「本番」に切り替える。

https://console.cloud.google.com/auth/audience →「アプリを公開」。

Calendar と Tasks は機密スコープなので、初回の認可時に「確認されていないアプリ」の
警告が出る。自分で作ったものなので「詳細」→「安全ではないページに移動」で進んでよい。

（Workspace アカウントで User Type を「内部」にした場合は、この制限はかからない。）

参考：[アプリの対象ユーザーを管理する](https://support.google.com/cloud/answer/15549945?hl=ja)

#### 「[ブランディング] ページで構成を完了する必要があります」と出るとき

ブランディング側に埋まっていない欄がある。上から順に確かめる。

1. **必須の3つが入っているか。** アプリ名／ユーザーサポートメール／デベロッパーの
   連絡先情報。入れたあと**ページ下部の「保存」を押す**。押し忘れが一番多い
2. **アプリのホームページ・プライバシーポリシー・利用規約を空にする。**
   ここに URL を入れると**承認済みドメイン**の登録が要る。さらにそのドメインは
   Search Console で所有確認が済んでいる必要がある。**個人のデスクトップアプリなら
   持っていないのが普通なので、3つとも空にしておく**（任意項目）
3. **ロゴを外す。** ロゴを載せるとブランド確認の対象になり、公開の前に審査が要る
4. 「承認済みドメイン」に何か残っていたら消す。2 を空にすれば要らない

それでも通らないときは、ブランディング画面に出ている赤字（どの欄が足りないか）を
そのまま読む。

#### どうしても「本番」にできないとき

「テスト」のままでも使える。ただし7日ごとに繋ぎ直しになる。

アプリ側はその場面を想定してある。更新に失敗したら控えを消し、
「Google との連携が切れました。接続し直してください。」と出す。繋がっているのに
何をしても失敗し続ける、という状態にはならない。

参考：[アクセス認証情報を作成する](https://developers.google.com/workspace/guides/create-credentials)

## 5. JSON を落としてアプリに読み込ませる

作成したクライアントの行から **JSON をダウンロード**する。
`client_secret_123-abc.apps.googleusercontent.com.json` のような名前のファイル。

アプリの右上の **⚙ →「Google のクライアント設定を読み込む…（JSON）」** で、そのファイルを選ぶ。

保存先は次の場所。手で置いてもよい。

```
%LocalAppData%\SlideinaCalendar\google-client.json
```

ID とシークレットを画面に手で写す作りにはしていない。長い文字列の写し間違いは、
認可が通らない形でしか現れず原因が分かりにくいため。

## シークレットの扱い

デスクトップアプリ型のクライアントシークレットは、**秘密として扱えない**。
配布物から読めるので Google もそう明記している。だからこのファイルは暗号化しない。

秘密にすべきなのは**認可のあとに受け取るトークン**のほう。こちらは OS の仕組みで
保護して保存する（Windows なら DPAPI）。

横取りへの備えは PKCE で行う。シークレットだけでは認可コードを交換できない。

## うまくいかないとき

| 症状 | 見るところ |
|---|---|
| 毎週つなぎ直しを求められる | 公開ステータスが「テスト」のまま。3 を見直す |
| 公開しようとすると「ブランディングで構成を完了」と出る | 3 の「ブランディングで構成を完了」を見る。URL 欄を空にするのが近道 |
| 「ウェブアプリ用の設定です」と出る | クライアントの種類が違う。4 を見直す |
| 認可画面で「確認されていないアプリ」 | 自分で作ったものなら進んでよい |
| 認可のあと何も起きない | 既定のブラウザが `127.0.0.1` を開けているか |

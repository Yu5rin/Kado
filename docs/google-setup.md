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

**URL 欄を空にしておけばよい、というのは誤り。** 外部の本番モードに切り替えるとき、
Google は次を求める（公開ボタンのツールチップに出る）。

> Valid app name, support email, homepage url, and privacy policy url are
> required for switching the app to external production mode.

つまり**ホームページ URL とプライバシーポリシー URL が必須**。エラー文がどの欄か
示さないので、埋まっているように見えて通らない。

必要なもの。

1. アプリ名 ／ ユーザーサポートメール ／ デベロッパーの連絡先情報
2. **アプリケーションのホームページ**（URL）
3. **プライバシーポリシー リンク**（URL）
4. **承認済みドメイン** … 2 と 3 のドメイン。
   [Search Console](https://search.google.com/search-console) での所有確認も要る

URL を置く先が無ければ `site/` の2枚を使う。置き方は `site/README.md` を見る。

（利用規約リンクとロゴは任意。ロゴを載せるとブランド確認の対象になるので、
不要なら外しておく。）

#### どうしても「本番」にできないとき

「テスト」のままでも使える。ただし7日ごとに繋ぎ直しになる。

アプリ側はその場面を想定してある。更新に失敗したら控えを消し、
「Google との連携が切れました。接続し直してください。」と出す。繋がっているのに
何をしても失敗し続ける、という状態にはならない。

参考：[アクセス認証情報を作成する](https://developers.google.com/workspace/guides/create-credentials)

## 4. クライアント ID を発行する

https://console.cloud.google.com/auth/clients

上部の **「＋ クライアントを作成」**（画面によっては「認証情報を作成」→
「OAuth クライアント ID」）を押す。

- **アプリケーションの種類** … **デスクトップ アプリ** を選ぶ
- **名前** … 何でもよい（例：`SlideinaCalendar`）

「作成」を押すと、クライアント ID とシークレットが表示される。**この画面で書き写す
必要はない。** 次の手順でファイルごと落とす。

### ⚠ 種類を間違えないこと

**「ウェブ アプリケーション」を選ぶと使えない。** このアプリは、認可が済んだことを
自分の PC（`127.0.0.1`）で受け取る作りになっている。ウェブアプリ型はリダイレクト先を
あらかじめ登録した URL に限るので、この受け取り方ができない。

アプリに読み込ませたときに「ウェブアプリ用の設定です」と断られたら、種類が違う。
デスクトップ アプリで作り直す。

## 5. JSON を落としてアプリに読み込ませる

### 5-1. JSON を落とす

https://console.cloud.google.com/auth/clients

一覧に、さきほど作ったクライアントの行がある。**その行の右端にある
ダウンロードのアイコン（↓）** を押す。

行をクリックして詳細を開いた場合は、画面の右上あたりに **「JSON をダウンロード」**
というボタンがある。どちらからでもよい。

落ちてくるのは、こういう名前のファイル。

```
client_secret_123456789012-abcdefghijklmnop.apps.googleusercontent.com.json
```

たいてい「ダウンロード」フォルダに入る。**中身を開く必要はない。**

### 5-2. アプリに読み込ませる

SlideinaCalendar を起動して、**画面の右上にある歯車（⚙）のボタン**を押す。
メニューが出るので、**「Google のクライアント設定を読み込む…（JSON）」** を選ぶ。

**「クライアント設定を選ぶ（client_secret_….json）」** という窓が開く。
5-1 で落としたファイルを選んで「開く」。

`.json` だけが並ぶようになっているので、ダウンロードフォルダを開けば
すぐ見つかるはず。

読み込めると、画面の下のほうに「Google のクライアント設定を取り込みました」と出る。
これで下ごしらえは終わり。

### 5-3. 繋ぐ

もう一度 **⚙ → 「Google に接続…」**。ブラウザが開いて Google の許可画面が出る。
許可すると、そのまま1回目の同期が走る。

---

読み込ませる代わりに、ファイルを自分でこの場所へ置いてもよい。

```
%LocalAppData%\SlideinaCalendar\google-client.json
```

（エクスプローラーのアドレス欄に `%LocalAppData%\SlideinaCalendar` と入れると開く。
フォルダが無ければ、アプリを一度起動すると作られる。）

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
| 公開しようとすると「ブランディングで構成を完了」と出る | ホームページ URL とプライバシーポリシー URL が必須。3 の該当項を見る |
| 「ウェブアプリ用の設定です」と出る | クライアントの種類が違う。4 を見直す |
| 認可画面で「確認されていないアプリ」 | 自分で作ったものなら進んでよい |
| 認可のあと何も起きない | 既定のブラウザが `127.0.0.1` を開けているか |
| どのファイルを落とすのか分からない | 5-1。クライアント一覧の行の右端、↓ のアイコン |
| ⚙ のメニューに何も出ない | 歯車は画面のいちばん右上。ツールバーの右端にある |
| 「Google に接続…」が押せない | 先に 5-2 でクライアント設定を読み込む |

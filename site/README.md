# 公開用のページ

Google の OAuth 同意画面を**本番**に切り替えるには、ホームページ URL と
プライバシーポリシー URL が要る。その2枚。

外部の本番モードに切り替えるとき、Google は次を求める。

> Valid app name, support email, homepage url, and privacy policy url are
> required for switching the app to external production mode.

「ブランディングの欄を空にしておけばよい」は**誤り**。空だと公開できない。

## 置き場所

**このリポジトリでは公開しない。** private なので GitHub Pages に有料プランが要るうえ、
public にすると要件書・モック・実働日の Excel・旧バックアップまで外から見えてしまう。

公開用の別リポジトリ（public）を作り、この2枚だけを置く。

```
Yu5rin/slideinacalendar-site   ← 公開。ここに index.html と privacy.html
Yu5rin/SlideinaCalendar        ← private。中身はそのまま
```

その後の手順。

1. 別リポジトリの Settings → Pages で、ブランチを `main` / フォルダーを `/ (root)` にする
2. `https://yu5rin.github.io/slideinacalendar-site/` が開けることを確認する
3. [Search Console](https://search.google.com/search-console) で `yu5rin.github.io` の
   所有確認を済ませる
4. Google Cloud の[ブランディング](https://console.cloud.google.com/auth/branding)に入れる
   - アプリケーションのホームページ … `https://yu5rin.github.io/slideinacalendar-site/`
   - プライバシーポリシー リンク … `https://yu5rin.github.io/slideinacalendar-site/privacy.html`
   - 承認済みドメイン … `yu5rin.github.io`
5. 保存してから[対象](https://console.cloud.google.com/auth/audience)で「アプリを公開」

## 中身について

プライバシーポリシーは実装に合わせて書いてある。書いてあることと動きが食い違うと
まずいので、データの扱いを変えたらこちらも直す。

- データは端末内にのみ保存する
- 作者や第三者のサーバーへは送信しない
- 通信先は Google の API のみ
- 解析・広告・トラッキングは入れない
- トークンは OS の仕組み（Windows では DPAPI）で保護する

要求するスコープと用途の対応も載せてある（要件書 6.1）。

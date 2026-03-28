# unifocl
![unifocl logo](https://github.com/user-attachments/assets/963ba3c2-5166-425c-8587-486753a41541)

ターミナルファーストなUnity開発コンパニオン。**unifocl**は、コマンドラインから直接Unityプロジェクトを操作・ナビゲートするための構造化された手法を提供します。

Unityエディタを置き換えることを目的としたものではありません。CLIやTUI（ターミナルユーザーインターフェース）を通じて、プロジェクト構造、アセット、ヒエラルキーの管理を行うことを好む開発者のための補助ツールとして機能します。

unifoclは独立したプロジェクトであり、Unity Technologiesとは一切関係なく、提携や推奨を受けているものではありません。

## 機能

Unityエディタのグラフィカルインターフェースのみに依存するのではなく、unifoclは以下の機能を提供します：

- **モードベースのナビゲーション:** ヒエラルキー、プロジェクト、インスペクターをナビゲートするための、コンテキストを認識する環境。
- **決定論的な操作:** コマンド駆動によるファイルおよびオブジェクト操作。
- **洗練されたインターフェース:** Spectre.Consoleで構築された、クリーンなCLI/TUI体験。

## インストール

GitHub Releases、Homebrew、またはWinget経由でインストールできます。

### GitHub Release

[最新のGitHubリリース](https://github.com/Kiankinakomochi/unifocl/releases/latest)からリリースアーティファクトをダウンロードしてください。

### Homebrew (macOS)

```
brew tap Kiankinakomochi/unifocl
brew install unifocl
```

### Winget (Windows)

Wingetへの登録は現在、コミュニティリポジトリでの承認待ちです（[Pull Request #350729](https://github.com/microsoft/winget-pkgs/pull/350729)）。

承認後、以下のコマンドでインストールできるようになります：

```
winget install Kiankinakomochi.unifocl
```

## コマンド＆機能ガイド

unifoclを起動すると、起動画面が表示されます。ここからCLIはインタラクティブシェルとして動作し、システムおよびライフサイクル操作には**スラッシュコマンド**（例: `/open`）を、コンテキストに沿ったプロジェクト操作には**標準コマンド**（例: `ls`, `cd`）を使用します。

### 1. システム・ライフサイクルコマンド

これらのコマンドは、セッション、プロジェクトの読み込み、およびCLI設定を管理します。スラッシュ（`/`）がプレフィックスとして付きます。

| **コマンド** | **エイリアス** | **説明** |
| --- | --- | --- |
| `/open <path> [--allow-unsafe]` | `/o` | Unityプロジェクトを開く。デーモンを起動/アタッチし、メタデータを読み込みます。 |
| `/close` | `/c` | 現在のプロジェクトからデタッチし、アタッチされたデーモンを停止します。 |
| `/quit` | `/q`, `/exit` | CLIクライアントを終了します（デーモンは実行したまま残ります）。 |
| `/daemon <start&#124;stop&#124;restart&#124;ps&#124;attach&#124;detach>` | `/d` | デーモンのライフサイクルコマンドを管理します。 |
| `/new <name> [version]` |  | 新しいUnityプロジェクトを立ち上げます。 |
| `/clone <git-url>` |  | リポジトリをクローンし、ローカルのCLIブリッジモード設定をセットアップします。 |
| `/recent [idx]` |  | 最近のプロジェクトを一覧表示するか、インデックスを指定して開きます。 |
| `/config <get/set/list/reset>` | `/cfg` | CLIの環境設定（テーマなど）を管理します。 |
| `/status` | `/st` | デーモン、モード、エディタ、プロジェクト、およびセッションのステータス概要を表示します。 |
| `/doctor` |  | 環境およびツールの診断を実行します。 |
| `/scan [--root <dir>] [--depth <n>]` |  | Unityプロジェクトのディレクトリをスキャンします。 |
| `/info <path?>` |  | Unityプロジェクトのメタデータとプロトコルの詳細を検査します。 |
| `/logs [daemon&#124;unity] [-f]` |  | デーモンのランタイム概要を表示するか、ログをフォロー（追跡）します。 |
| `/examples` |  | 一般的な操作コマンドのフローを表示します。 |
| `/update` |  | インストールされているCLIバージョンとアップデートのガイダンスを表示します。 |
| `/install-hook` |  | 現在/開いているプロジェクトに対して、ブリッジ依存関係のインストールフロー（`/init`）を実行します。 |
| `/unity detect` |  | インストールされているUnityエディタを一覧表示します。 |
| `/unity set <path>` |  | デフォルトのUnityエディタパスを設定します。 |
| `/build run [target] [--dev] [--debug] [--clean] [--path <output-path>]` | `/b` | Unityプレイヤーのビルドをトリガーします。ターゲットを省略した場合は、インタラクティブなターゲットセレクタから選択します。 |
| `/build exec <Method>` | `/bx` | 静的なビルドメソッドを実行します（例: `CI.Builder.BuildAndroidProd`）。 |
| `/build scenes` |  | インタラクティブなTUIを開き、ビルドシーンの表示、切り替え、並べ替えを行います。 |
| `/build addressables [--clean] [--update]` | `/ba` | Addressablesコンテンツのビルドをトリガーします（フルまたはアップデートモード）。 |
| `/build cancel` |  | デーモン経由でアクティブなビルドプロセスのキャンセルを要求します。 |
| `/build targets` |  | このUnityエディタで現在利用可能なプラットフォームのビルドサポートを一覧表示します。 |
| `/build logs` |  | ライブビルドログの末尾を再度開きます（再開可能、エラーフィルタリング付き）。 |
| `/upm` |  | Unity Package Managerコマンドの使用法とオプションを表示します。 |
| `/upm list [--outdated] [--builtin] [--git]` | `/upm ls` | インストールされているUnityパッケージを一覧表示します（outdated/builtin/gitフィルタオプションあり）。 |
| `/upm install <target>` | `/upm add`, `/upm i` | パッケージID、Git URL、または `file:` ターゲットでパッケージをインストールします。 |
| `/upm remove <id>` | `/upm rm`, `/upm uninstall` | パッケージIDでパッケージを削除します。 |
| `/upm update <id> [version]` | `/upm u` | パッケージを最新または指定されたバージョンにアップデートします。 |
| `/prefab create <idx\|name> <asset-path>` |  | シーン内のGameObjectを新しいPrefabアセットとしてディスクに保存します。 |
| `/prefab apply <idx>` |  | インスタンスのオーバーライドをソースPrefabアセットに反映します。 |
| `/prefab revert <idx>` |  | ローカルのオーバーライドを破棄し、ソースPrefabアセットの状態に戻します。 |
| `/prefab unpack <idx> [--completely]` |  | Prefab接続を切断し、通常のGameObjectに変換します。 |
| `/prefab variant <source-path> <new-path>` |  | ベースPrefabを継承するPrefab Variantを作成します。 |
| `/init [path]` |  | Unityバッチライフサイクルを通じて、ブリッジモード設定の生成、エディタ側の依存関係のインストール、および必須のMCPパッケージのインストールを行います。 |
| `/keybinds` | `/shortcuts` | モーダルなキーバインドとショートカットを表示します。 |
| `/version` |  | CLIとプロトコルのバージョンを表示します。 |
| `/protocol` |  | サポートされているJSONスキーマの機能を表示します。 |
| `/dump <hierarchy&#124;project&#124;inspector> [--format json&#124;yaml] [--compact] [--depth n] [--limit n]` |  | エージェントワークフローのための決定論的モード状態をダンプします。 |
| `/clear` |  | 起動画面とログをクリアして再描画します。 |
| `/help [topic]` | `/?` | トピック別（`root`, `project`, `inspector`, `build`, `upm`, `daemon`）にヘルプを表示します。 |

**挙動に関するメモとプロトコルの安全性確保:**

- サブコマンドなしの `/daemon` は、使用法とプロセスの概要を返します。
- サポートされていないスラッシュコマンドのルートは、明示的に `unsupported route` メッセージを返します。
- **ホストモードヒエラルキーフォールバック**は、GUIブリッジがアタッチされていない場合に利用可能です：
    - `HIERARCHY_GET` は `Assets` ルートのスナップショットを返します。
    - `HIERARCHY_FIND` はノード名/パスのあいまい検索を行います。
    - `HIERARCHY_CMD` はガードレール付きで `mk`, `rm`, `rename`, `mv`, `toggle` をサポートします。
    - *ホストモードフォールバックの安全制約:* 全ての変更は `Assets` 内に制限されます。移動/リネームによるパスのエスケープは拒否されます。ディレクトリをそれ自身や子孫の中に移動することは拒否されます。`mk`は名前を検証し、型付きのプレースホルダー（`Empty`, `EmptyChild`, `EmptyParent`, `Text/TMP`, `Sprite`、デフォルトのプレハブ）をサポートします。
- 永続的なプロジェクト変更がサポートされており（`submit -> status -> result`）、進行中のHTTPレスポンスがUnityの更新・コンパイル・ドメインリロードによって中断された場合でも、変更結果を照会可能です。
- **Unity MCP パッケージ:** unifocl は [unity-mcp](https://github.com/CoplayDev/unity-mcp) パッケージを利用します。このパッケージは unifocl がプロジェクトを初期化する際にインストールされます。このツールは **Python 3.10以上** と [**uv**](https://github.com/astral-sh/uv) に依存しています。unifoclはHomebrew（macOS）またはWinget（Windows）を使用して、これらの依存関係を解決およびインストールするよう最善を尽くします。直接インストールする場合は、[MCPForUnity Git target](https://github.com/CoplayDev/unity-mcp.git?path=/MCPForUnity#main)（OpenUPM パッケージID: `com.coplaydev.unity-mcp`）を使用してください。
- **MCP ブリッジエンドポイント:** `POST /mcp/unifocl_project_command`（操作：`submit`, `get_status`, `get_result`, `cancel`）
- **永続的な HTTP フォールバックエンドポイント:** `POST /project/mutation/submit`, `GET /project/mutation/status?requestId=<id>`, `GET /project/mutation/result?requestId=<id>`, `POST /project/mutation/cancel?requestId=<id>`

### 2. デーモン管理

デーモンはプロジェクトとの永続的な接続を維持します。`/daemon`（または `/d`）コマンドスイートを使用して管理します。

| **サブコマンド** | **説明** |
| --- | --- |
| `start` | デーモンを起動します。受け付けるフラグ: `--port`, `--unity <path>`, `--project <path>`, `--headless`（ホストモード）, `--allow-unsafe`。 |
| `stop` | このCLIによって制御されているデーモンインスタンスを停止します。 |
| `restart` | 現在アタッチされているデーモンを再起動します。 |
| `ps` | 実行中のデーモンインスタンス、ポート、稼働時間、関連付けられたプロジェクトを一覧表示します。 |
| `attach <port>` | 指定されたポートの既存のデーモンにCLIをアタッチします。 |
| `detach` | CLIをデタッチしますが、デーモンはバックグラウンドで起動させたままにします。 |

**並列自律エージェントに関するメモ:** 並列で動作する自律エージェントの場合、隔離されたGitのworktreeをプロビジョニングし、動的なポートマッピングを使用してworktreeごとにデーモンを起動してください。

- Bashワークフロー: `src/unifocl/scripts/agent-worktree.sh`
- PowerShellワークフロー: `src/unifocl/scripts/agent-worktree.ps1`
- `origin/main` から専用のブランチworktreeをプロビジョニングします。変更可能なworktreeをエージェント間で共有しないでください。
- 必要に応じて、デーモンの起動前にウォームアップ済みのUnity `Library` キャッシュをコピーしてください。
- デーモンのポートを動的に割り当て、設定した `http://127.0.0.1:<dynamic-port>/ping` ヘルスチェックエンドポイントを通じて準備完了を確認してください。
- 全ての変更は、プロビジョニングされた各worktreeのスコープ内に維持してください。
- 完了後のティアダウン: デーモンを停止した後、`git worktree remove --force <path>` と `git worktree prune` を実行します。
- 運用上の境界: worktree間の相互編集は禁止、変更可能なデーモンの状態の共有は禁止、デーモンポートの再利用を前提としないこと。
- マイルストーントラッキングのストリーム: `Worktree Isolation and Multi-Agent Daemon Safety`
- スモークテスト用プロジェクトのデフォルト: `setup-smoke-project` は `Packages/manifest.json` に `com.unity.modules.imageconversion` をシードします。

### 3. モード切り替え

プロジェクトを開いたら、これらのコマンドを使用してアクティブなコンテキストを切り替えます。

| **コマンド** | **エイリアス** | **説明** |
| --- | --- | --- |
| `/project` | `/p` | プロジェクトモード（アセット構造のナビゲーション）に切り替えます。 |
| `/hierarchy` | `/h` | ヒエラルキーモード（シーン構造のTUI）に切り替えます。 |
| `/inspect <idx/path>` | `/i` | インスペクターモードに切り替え、ターゲットにフォーカスします。 |

### 4. コンテキスト操作（非スラッシュコマンド）

特定のモード（プロジェクト、ヒエラルキー、またはインスペクター）内にいる場合、スラッシュを省略してアクティブな環境と直接対話します。変更を伴う操作は、利用可能な場合はブリッジモード経由で、該当する場合はホストモードのフォールバック経由で安全にルーティングされます。

| **コマンド** | **エイリアス** | **説明** |
| --- | --- | --- |
| `list` | `ls` | 現在のアクティブなコンテキスト内のエントリを一覧表示します。 |
| `enter <idx>` | `cd` | インデックスで選択されたノード、フォルダ、またはコンポーネントに入ります。 |
| `up` | `..` | 親のレベルへ1つ上がります。 |
| `make <type> <name>` | `mk` | アイテムを作成します（例: `mk script Player`, `mk gameobject`）。 |
| `load <idx/name>` |  | シーン、プレハブ、またはスクリプトを読み込み/開きます。 |
| `remove <idx>` | `rm` | 選択したアイテムを削除します。 |
| `rename <idx> <new>` | `rn` | 選択したアイテムの名前を変更します。 |
| `set <field> <val>` | `s` | フィールドまたはプロパティの値を設定します。 |
| `toggle <target>` | `t` | boolean/active/enabledフラグを切り替えます。 |
| `move <...>` | `mv` | アイテムの移動、親の変更、順序の変更を行います。 |
| `f [--type <type>&#124;t:<type>] <query>` | `ff` | アクティブなモードであいまい検索（Fuzzy find）を実行します。 |
| `inspect [idx&#124;path]` |  | インスペクターコンテキストからインスペクタールートターゲットに入ります。 |
| `edit <field> <value...>` | `e` | 選択したコンポーネント（インスペクター）のシリアライズされたフィールド値を編集します。 |
| `component add <type>` | `comp add <type>` | 検査対象のオブジェクトにコンポーネントを追加します。 |
| `component remove <index&#124;name>` | `comp remove <index&#124;name>` | 検査対象のオブジェクトからコンポーネントを削除します。 |
| `scroll [body&#124;stream] <up&#124;down> [count]` |  | インスペクターの本文またはコマンドストリームをスクロールします。 |
| `upm list [--outdated] [--builtin] [--git]` | `upm ls` | プロジェクトモードで、インストールされているUnityパッケージを一覧表示します。 |
| `upm install <target>` | `upm add`, `upm i` | プロジェクトモードで、ID、Git URL、または `file:` ターゲットによるパッケージをインストールします。 |
| `upm remove <id>` | `upm rm`, `upm uninstall` | プロジェクトモードで、パッケージIDによるパッケージを削除します。 |
| `upm update <id> [version]` | `upm u` | プロジェクトモードで、パッケージを最新または指定されたバージョンにアップデートします。 |
| `build run [target] [--dev] [--debug] [--clean] [--path <output-path>]` | `b` | プロジェクトモードでUnityビルドを実行します。 |
| `build exec <Method>` | `bx` | プロジェクトモードで静的なビルドメソッドを実行します。 |
| `build scenes` |  | プロジェクトモードでシーンビルド設定のTUIを開きます。 |
| `build addressables [--clean] [--update]` | `ba` | プロジェクトモードでAddressablesコンテンツをビルドします。 |
| `build cancel` |  | プロジェクトモードでアクティブなビルドのキャンセルを要求します。 |
| `build targets` |  | プロジェクトモードでUnityビルドサポートターゲットを一覧表示します。 |
| `build logs` |  | プロジェクトモードで再開可能なビルドログの末尾を開きます。 |
| `prefab create <idx\|name> <asset-path>` |  | プロジェクトモードでシーンGameObjectを新しいPrefabアセットに変換します。 |
| `prefab apply <idx>` |  | プロジェクトモードでインスタンスのオーバーライドをソースPrefabに反映します。 |
| `prefab revert <idx>` |  | プロジェクトモードでオーバーライドを破棄しソースPrefabに戻します。 |
| `prefab unpack <idx> [--completely]` |  | プロジェクトモードでPrefab接続を切断します。 |
| `prefab variant <source-path> <new-path>` |  | プロジェクトモードでベースPrefabからVariantを作成します。 |

### 5. あいまい検索とIntellisense

unifocl は Intellisense を備えたコンポーザーを備えています。

- `/` を入力すると、スラッシュコマンドの候補パレットが開きます。
- 標準的なテキストを入力すると、プロジェクトモードの候補が表示されます。

**あいまい検索（Fuzzy Finding）:** プロジェクトまたはインスペクター全体であいまい検索をトリガーするには、`f` または `ff` コマンドを使用します。プロジェクトモードでは、`--type` / `-t` または `t:<type>` を使用して検索スコープを絞り込むことができます。

- **構文:** `f [--type <type>|-t <type>|t:<type>] <query>`
- **サポートされているタイプ:** `script`, `scene`, `prefab`, `material`, `animation`
- **例:** `f --type script PlayerController`

### 6. キーバインドとフォーカスモード

CLIは、インデックスを入力せずにリストや構造をナビゲートし、操作するためのキーボード駆動のナビゲーションを提供します。

**グローバルキーバインド**

- **`F7`**: ヒエラルキーTUI、プロジェクトナビゲーター、最近のプロジェクトリスト、およびインスペクターのフォーカスを切り替えます。
- **`Esc`**: Intellisenseを閉じる、またはすでに閉じている場合は入力をクリアします。
- **`↑` / `↓`**: あいまい検索/Intellisenseの候補をナビゲートします。
- **`Enter`**: 選択した候補を挿入するか、入力を確定します。

**コンテキスト別のフォーカスナビゲーション** フォーカスされた状態（`F7`）では、矢印キーとTabキーがコンテキストに応じて動作します：

| **アクション** | **ヒエラルキーフォーカス** | **プロジェクトフォーカス** | **インスペクターフォーカス** |
| --- | --- | --- | --- |
| **`↑` / `↓`** | ハイライトされたGameObjectを移動 | ハイライトされたファイル/フォルダを移動 | ハイライトされたコンポーネント/フィールドを移動 |
| **`Tab`** | 選択したノードを展開 | 選択したエントリを表示/開く | 選択したコンポーネントを検査 |
| **`Shift+Tab`** | 選択したノードを折りたたむ | 親フォルダへ移動 | コンポーネントリストに戻る |
| **フォーカスを抜ける** | `Esc` または `F7` | `Esc` または `F7` | `Esc` または `F7` |

## 高度な機能

### ドライラン（Dry-Run）プレビュー

- すべてのインタラクティブモードでの変更コマンドにおいて、`dry-run` がサポートされるようになりました：
- `ヒエラルキー` の変更（`mk`, `toggle`, `rm`, `rename`, `mv`）
- `インスペクター` の変更（`set`, `toggle`, `component add/remove`, `make`, `remove`, `rename`, `move`）
- `プロジェクト` のファイルシステム変更（`mk-script`, `rename-asset`, `remove-asset`）

挙動：

- **ヒエラルキー / インスペクター（メモリレイヤー）:** unifoclは変更前/変更後の状態スナップショットを取得し、Undoグループ内で実行し、即座にリバート（元に戻す）して、構造化された差分プレビューを返します。
- **プロジェクト（ファイルシステムレイヤー）:** unifoclはファイルI/Oを実行せずに、提案されたパス/メタデータの変更を返します。
- **TUIレンダリング:** `dry-run` が追加されると、コマンドトランスクリプトの出力に統合差分（unified diff）の行が追加されます。

例:

```
# ヒエラルキーモード
mk Cube --dry-run
rename 12 NewName --dry-run

# インスペクターモード
set speed 5 --dry-run
component add Rigidbody --dry-run

# プロジェクトモード
rename 3 PlayerController --dry-run
rm 7 --dry-run
```

### エージェントモード（機械向けワークフロー）

unifoclは、インタラクティブなTUIの挙動ではなく決定論的なI/Oを必要とするLLM、自動化、およびツールラッパー向けの**エージェント実行パス**をサポートしています。

基本原則：

- すべてのコマンドに対する構造化されたレスポンスエンベロープ（包み）。
- エージェントのワンショットモードではSpectre/TUIレンダリングを行わない。
- 標準化されたエラー分類とプロセスの終了コード。
- コンテキストのハイドレーション（復元）のための明示的な状態シリアライズコマンド。

### 1. エージェント向けワンショット CLI

1つのコマンドを実行して終了するには、`exec` を使用します：

```
unifocl exec "<command>" [--agentic] [--format json|yaml] [--project <path>] [--mode <project|hierarchy|inspector>] [--attach-port <port>] [--request-id <id>]
```

例:

```
unifocl exec "/version" --agentic --format json
unifocl exec "/protocol" --agentic --format yaml
unifocl exec "/dump project --format json --depth 2 --limit 5000" --agentic --project /path/to/UnityProject
unifocl exec "upm list --outdated" --agentic --project /path/to/UnityProject --mode project
```

備考:

- `agentic` は機械出力（単一のレスポンスペイロード）を有効にします。
- `format` はペイロードのエンコーディング（`json` または `yaml`）を制御します。
- `project`, `mode`, `attach-port` はランタイムコンテキストをシードするため、コマンドをインタラクティブなセットアップなしで実行できます。

### 2. 統合エージェントエンベロープ

- `agentic` のレスポンスは以下の1つのスキーマを使用します：

```
{
  "status": "success|error",
  "requestId": "string",
  "mode": "project|hierarchy|inspector|none",
  "action": "string",
  "data": {},
  "errors": [{ "code": "E_*", "message": "string", "hint": "string|null" }],
  "warnings": [{ "code": "W_*", "message": "string" }],
  "diff": {
    "format": "unified",
    "summary": "string|null",
    "lines": ["--- before", "+++ after", "..."]
  },
  "meta": {
    "schemaVersion": "agentic.v1",
    "protocol": "v3",
    "exitCode": 0,
    "timestampUtc": "ISO-8601 UTC",
    "extra": {}
  }
}
```

フィールドのセマンティクス：

- `status`: 高レベルの結果（`success` または `error`）。
- `requestId`: 呼び出し元が提供する相関ID（省略された場合は生成されます）。
- `mode`: コマンド実行後の有効なランタイムコンテキスト。
- `action`: 正規化されたコマンドファミリー（例: `version`, `dump`, `upm`）。
- `data`: コマンドペイロード（アクションによって形状が異なります）。
- `errors`: 決定論的な機械エラー（成功時は空）。
- `warnings`: 致命的ではない問題。
- `diff`: オプションのドライランの差分ペイロード（`dry-run` プレビューが返された場合に存在します）。
- `meta`: スキーマ/プロトコル/終了メタデータに加えて、オプションのコマンド固有の追加情報。

エージェントVCSセットアップのガード：

- UVCSが検出されたものの、プロジェクトのVCSセットアップが不完全な場合、エージェントによるプロジェクトの変更は `E_VCS_SETUP_REQUIRED` で直ちに中止（ショートサーキット）されます。
- 変更を伴わないエージェントコマンドは引き続き実行されます。

### 3. エージェント終了コード

| **終了コード** | **意味** |
| --- | --- |
| `0` | 成功 |
| `2` | 検証 / パース / コンテキスト状態のエラー |
| `3` | デーモン/ブリッジの可用性またはタイムアウトクラスの失敗 |
| `4` | 内部実行エラー |
| `6` | エスカレーションが必要（サンドボックス/ネットワーク制限が実行を妨げた可能性が高い） |

`E_VCS_SETUP_REQUIRED` は終了コード `2` に分類されます。`E_ESCALATION_REQUIRED` は終了コード `6` に分類されます。

### 4. `/dump` 状態のシリアライズ

`/dump` は、コンテキストウィンドウの転送と決定論的スナップショットのために設計されています：

```
/dump <hierarchy|project|inspector> [--format json|yaml] [--compact] [--depth n] [--limit n]
```

現在の挙動：

- `hierarchy`: アタッチされたデーモンからヒエラルキーのスナップショットを取得します。
- `project`: 決定論的な `Assets` ツリーのエントリをシリアライズします。
- `inspector`: アタッチされたブリッジパスからインスペクターのコンポーネント/フィールドをシリアライズします。

コンテキストの処理：

- 必要なランタイム状態が欠落している場合（例えば、`hierarchy` 用のアタッチされたデーモンがない場合）、レスポンスは修正のヒントと共に `E_MODE_INVALID` を返します。
- サポートされていないカテゴリーは `E_VALIDATION` を返します。

### 5. デーモン・エージェント HTTP エンドポイント

デーモンのサービスモードは、localhost にエージェントエンドポイントを公開します：

- `POST /agent/exec`
- `GET /agent/capabilities`
- `GET /agent/status?requestId=...`
- `GET /agent/dump/{hierarchy|project|inspector}?format=json|yaml`

例:

```
curl -X POST "http://127.0.0.1:8080/agent/exec" \
  -H "Content-Type: application/json" \
  -d '{
    "commandText": "/version",
    "contextMode": "project",
    "sessionSeed": "",
    "outputMode": "json",
    "requestId": "req-001"
  }'
```

デーモン側のエージェントエンドポイントは、同じ `exec --agentic` パスウェイに委譲されるため、CLIとHTTPの機械出力は契約の一貫性を保ちます。

### 6. エラーの分類

| **エラーコード** | **意味** |
| --- | --- |
| `E_PARSE` | コマンドのパース/ペイロード構文の失敗 |
| `E_MODE_INVALID` | 現在のコンテキストではコマンドを実行できない |
| `E_NOT_FOUND` | 要求されたオブジェクト/アセット/コンポーネントが見つからない |
| `E_TIMEOUT` | 操作がタイムアウトした |
| `E_UNITY_API` | デーモン/ブリッジのUnity実行パスの失敗 |
| `E_VCS_SETUP_REQUIRED` | インタラクティブなUVCSセットアップが完了するまで変更がブロックされる |
| `E_ESCALATION_REQUIRED` | コマンドがサンドボックス/ネットワークによってブロックされた可能性が高く、権限を上げて再実行する必要がある |
| `E_VALIDATION` | セマンティックな検証に失敗した |
| `E_INTERNAL` | 処理されていないランタイムエラー |

### 7. 機能のディスカバリーと OpenAPI

ランタイム機能のディスカバリー：

```
unifocl exec "/protocol" --agentic --format json
curl "http://127.0.0.1:8080/agent/capabilities"
```

静的 OpenAPI 契約：

- `docs/openapi-agentic.yaml`

### 8. 並行 Worktree 統合 (並列エージェント)

エージェントモードは、各エージェントを独自の一意なworktreeとデーモンポートに分離することで、複数の自律エージェント間で安全に実行できるように設計されています。

組み込みのオーケストレーションスクリプトを使用してください：

- Bash: `src/unifocl/scripts/agent-worktree.sh`
- PowerShell: `src/unifocl/scripts/agent-worktree.ps1`

推奨フロー（bashの例）：

```
# 1) origin/main から分離された worktree + ブランチをプロビジョニング
src/unifocl/scripts/agent-worktree.sh provision \
  --repo-root . \
  --worktree-path ../unifocl-agent-a \
  --branch codex/agent-a

# 2) エージェントスモークテスト用の最小構成のUnityプロジェクトの雛形を作成
src/unifocl/scripts/agent-worktree.sh setup-smoke-project \
  --worktree-path ../unifocl-agent-a \
  --project-path .local/agentic-smoke-project

# 3) ワンショットエージェント実行経由でブリッジのinitを実行（インタラクティブシェルなし）
src/unifocl/scripts/agent-worktree.sh init-smoke-agentic \
  --worktree-path ../unifocl-agent-a \
  --project-path .local/agentic-smoke-project \
  --format json

# 4) その worktree/project 用に動的に選択された空きポートでデーモンを起動
src/unifocl/scripts/agent-worktree.sh start-daemon \
  --worktree-path ../unifocl-agent-a \
  --project-path .local/agentic-smoke-project

# 5) その隔離されたワークスペースで決定論的な機械コマンドを実行
cd ../unifocl-agent-a
dotnet run --project src/unifocl/unifocl.csproj -- \
  exec "/dump project --format json --depth 2 --limit 2000" \
  --agentic --project "$(pwd)/.local/agentic-smoke-project" --mode project
```

並行処理のセーフガード：

- 1エージェント = 1ブランチ + 1worktree。
- 1worktree = 1デーモンポート。
- 複数のエージェントが同じworktreeを同時に変更することは絶対に避けてください。
- 完了したworktreeはスクリプト（`teardown`）または `git worktree remove --force` によって破棄（ティアダウン）します。

## アーキテクチャとコアシステム

### アプリケーション・アーキテクチャ

unifoclは、クロスプラットフォーム（Windows, macOS, Linux）向けに構築された .NET コンソールアプリケーションです。アプリケーションは主に4つのレイヤーに分かれています：

1. **CLI レイヤー:** コマンドと構造化されたユーザー対話を処理します。
2. **モードシステム:** コンテキストを認識する環境（ヒエラルキー、プロジェクト、インスペクター）を管理します。
3. **デーモンレイヤー:** プロジェクトの状態を追跡する永続的なバックグラウンドのコーディネーター。
4. **ブリッジモードチャネル:** デーモンとアクティブなUnityエディタ/ランタイム間の通信インターフェース。

### unifocl デーモン

デーモンはlocalhostの制御プロセスであり、カーネル/OSレベルのファイル変更サービスではありません。

現在の実装の要約：

- CLIは、ライフサイクルおよびプロジェクトコマンドのために、ローカルHTTP（`127.0.0.1:<port>`）を介してデーモンエンドポイントと通信します。
- デーモンはプロジェクトにスコープされたセッションをウォームアップ状態に保つため、コマンドを実行するたびにUnityをコールドスタートさせる必要はありません。
- モードの選択はランタイムベースで行われます：
    - **ホストモード:** 適切なGUIエディタブリッジがアタッチされていない場合、unifoclはUnityをバッチ/グラフィックスなしモード（`headless`）で起動し、そのUnityプロセスを介してコマンドを提供します。
    - **ブリッジモード:** 同じプロジェクトのGUI Unityエディタがすでにアクティブでアタッチ可能な場合、unifoclはコマンドをそのライブエディタのブリッジエンドポイントにルーティングします。
- プロジェクト操作はUnity側のサービス/契約によって実行され、その後CLIに型付けされたレスポンスとして報告されます。
- エンドポイントが到達可能であっても不健全な場合（例えば、pingは通るがプロジェクトコマンドが通らない場合）、unifoclは管理対象のデーモンパスを再起動して再アタッチします。
- デーモンの状態はプロジェクトごと（決定論的ポート + ローカルの `.unifocl` 設定/セッションメタデータ）に追跡されます。

実際の意味するところ：

- unifoclは特権的なOSフックによってUnityをバイパスしません。
- 状況に応じて、ホストモードのUnityランタイムまたはブリッジモードのアタッチされたエディタランタイムのいずれかを介して実行されます。

### 永続化の安全契約

unifocl は、`hierarchy`、`inspector`、および `project` モード全体で変更に対する安全契約（Mutation Safety Contract）を強制します。実装は4つのレイヤーに分かれています。

### 1. トランザクション・エンベロープ（デーモンコア）

すべての変更リクエストは、Unity API またはファイルシステムでの実行前に、必須の `MutationIntent` エンベロープ（意図の包み）を伴います。

現在のエンベロープフィールド：

- `transactionId`
- `target`
- `property`
- `oldValue`
- `newValue`
- `flags.dryRun`
- `flags.requireRollback`（`true` である必要があります）
- `flags.vcsMode`（オプション: `uvcs_all` または `uvcs_hybrid_gitignore`）
- `flags.vcsOwnedPaths[]`（チェックアウトポリシーに使用される、パスごとのオプションの所有者メタデータ）

デーモン側の検証は `DaemonMutationTransactionCoordinator` に一元化されており、欠落しているか無効な変更リクエストを拒否します。有効なインテント（意図）は、モードごとに決定論的な安全ハンドラーにルーティングされます：

- `hierarchy` / `inspector` -> `memory`
- `project` -> `filesystem`

各変更エントリポイントは、コマンドの実行が続行される前に、統合されたトランザクション決定エンベロープ（`success|error`）を返します。

### 2. メモリレイヤーの安全性（ヒエラルキー＆インスペクター）

インスペクターとヒエラルキーのプロパティの書き込みは、UnityのシリアライズAPIを介してルーティングされ、冪等性（idempotency）が保証されます：

- 変更は `SerializedObject` / `SerializedProperty` を使用します。
- 書き込み前の読み取りチェックにより、変更のない（no-op）書き込みをスキップします。
- `Undo.RecordObject(...)` + `ApplyModifiedProperties()` は、値が実際に変更された場合のみ実行されます。

ライフサイクルおよび複数ステップのメモリ変更は、Undoの境界（Undo boundaries）でラップされます：

- 作成は `Undo.RegisterCreatedObjectUndo(...)` を使用します。
- 削除は `Undo.DestroyObjectImmediate(...)` を使用します。
- 複数ステップの操作は、成功時に `Undo.CollapseUndoOperations(groupId)` でグループ化されたUndoを使用します。
- 失敗した場合は `Undo.RevertAllDownToGroup(groupId)` を介して元に戻します。

シーン/プレハブの完全性のための永続化フック：

- プレハブのインスタンスは `PrefabUtility.RecordPrefabInstancePropertyModifications(...)` で追跡されます。
- 成功したシーンの変更はマークされ、`EditorSceneManager.MarkSceneDirty(...)` およびシーンの永続化サービスを通じて保存されます。
- ドライランモードでは、永続的なシーンの書き込みが抑制されます。

### 3. ファイルシステムレイヤーの安全性（プロジェクトモード）

UnityのUndoをバイパスするプロジェクトモードの変更は、トランザクションのスタッシュ（一時退避）とVCS対応のプリフライト（事前チェック）で保護されています：

- 実行前に、UVCSが所有するパスはチェックアウトのためにプリフライトされます（チェックアウトファースト・ポリシー。チェックアウトが利用できない場合は変更が失敗します）。
- 所有権モードはプロジェクトごとに解決されます：
    - `uvcs_all`: 全ての変更ターゲットがUVCS所有として扱われます。
    - `uvcs_hybrid_gitignore`: 所有権はパスレベルの `.gitignore` ルールから解決されます。
- 実行前に、対象のアセットと一致する `.meta` ファイルは、ランタイムスタッシュストレージ `$(UNIFOCL_PROJECT_STASH_ROOT || <temp>/unifocl-stash)/<project-hash>/...` の下にシャドウコピーされます。
- 成功時、スタッシュの内容は削除されます（コミットパス）。
- 失敗または例外時、スタッシュが復元され、クリーンアップターゲットが削除された後、`AssetDatabase.Refresh(ForceUpdate)` が呼び出されてUnityの状態が再同期されます。

Unity Version Control (旧 Plastic SCM) の挙動：

- UVCSはチェックアウトセマンティクスを使用するため、書き込み可能なファイルシステムの状態だけでは安全な変更のための権限として扱われません。
- unifoclはターゲットパスごとの所有権を解決し、ファイル変更が試みられる前にチェックアウトのプリフライトを実行します。
- UVCS所有と分類されたパスは、まずチェックアウトのプリフライトをパスする必要があります。そうでない場合、ファイルI/Oが開始される前に変更が拒否されます。
- `uvcs_hybrid_gitignore` モードでは、`.gitignore` が実用的な所有権の分割として使用されるため、UVCS所有と見なされるパスに対してのみUVCSチェックアウトが強制されます。
- ドライランには所有権とチェックアウトのヒントが含まれるため、自動化によって実行前に変更の実行可能性を検証できます。

インタラクティブセットアップガード：

- UVCSが自動検出されたものの未設定の場合、最初のプロジェクト変更で1度限りのVCSセットアップを促し、`.unifocl/vcs-config.json` を保存します。
- セットアップが拒否された場合、実用的なガイダンスと共に変更は中止されます。

ファイルシステムの重要な変更セクションは、スタッシュ/復元および変更実行時の並行による競合状態（race conditions）を回避するために、`SemaphoreSlim` で直列化されます。

### 4. ドライラン＆プレビューのメカニズム

ドライランの挙動は、CLIパースからデーモンの実行、およびエージェントレスポンスまで一貫して接続されています。

メモリのドライラン（`hierarchy` / `inspector`）：

- `EditorJsonUtility.ToJson(...)` を使用して変更前の状態をスナップショット。
- Undoグループ内で変更を実行。
- 変更後の状態をスナップショット。
- Undoを使用して即座に元に戻す。
- 構造化された統合差分（unified diff）ペイロードを返す。

ファイルシステムのドライラン（`project`）：

- `System.IO` の変更は発生しません。
- デーモンは提案されたパスおよびメタデータの変更（`.meta` の副作用を含む）と、各パス変更の所有権/チェックアウトヒントを返します。

CLI / エージェント統合：

- インタラクティブな出力では、Spectreのコマンドログに統合されたドライランの差分行を追加します。
- `agentic.v1` エンベロープには、機械消費者向けのオプションの `diff` ペイロード（`format`, `summary`, `lines`）が含まれます。

## 開発とコントリビューション

### ローカルの互換性チェック（Compatcheck）ブートストラップ

ローカルでUnityエディタの互換性チェックを実行する必要がある場合（特にブリッジ/エディタのコード変更後）、以下を使用してください：

```
./scripts/setup-compatcheck-local.sh
```

このコマンドが実行すること：

- ローカルのUnityエディタのインストールを検出します。
- `.local/compatcheck-benchmark` の下にベンチマーク用Unityプロジェクトを作成/立ち上げます。
- ローカルパスの設定を `local.config.json` に書き込みます。
- 次のコマンドを実行します：`dotnet build src/unifocl.unity.compatcheck/unifocl.unity.compatcheck.csproj --disable-build-servers -v minimal`

ローカルアーティファクトは意図的にコミットから除外されています（`local.config.json`, `.local/`）。

### コントリビューションとライセンス

外部からのコントリビューションは、バージョン 0.3.0 以降で受け付けています。

明示的に別段の定めがない限り、バージョン 0.3.0 以降に含まれることを意図して提出されたコントリビューションは、Apache License 2.0 の下でライセンスされます。

Apache License 2.0 は、バージョン 0.3.0 およびそれ以降のすべてのバージョンに適用されます。

バージョン 0.3.0 より前のすべてのコンテンツはプロプライエタリ（独自の所有権）であり、すべての権利が留保されています。

# ExecV2 実装進捗ドキュメント

次のエージェントが読むことを想定した引き継ぎ資料。

---

## ゴール

`/agent/exec` のフリーフォーム CommandText 実行を廃止し、スキーマ化された ExecV2 に置き換える。
承認フロー・リスク分類・セッション管理を段階的に追加し、最終的に IPC (UDS) に移行する。

詳細設計: `~/.claude/plans/partitioned-conjuring-curry.md`

---

## 作業ルール

- worktree: `/Users/makinokinichianju/Documents/GitHub/unifocl-exec-v2`
- ブランチ命名: `feature/exec-v2-sprint{N}-{short}`
- 各 Sprint 完了でブランチをコミット→プッシュ→PR 作成 (Sprint 単位)

---

## Sprint 進捗

### ✅ Sprint 1: 構造化 exec 基盤 + 危険操作承認制御
**ブランチ**: `feature/exec-v2-sprint1-structured-exec`
**コミット**: `c93871a`

**追加ファイル**:
- `src/unifocl/Models/ExecV2Models.cs` — ExecV2Request/Response, ExecRiskLevel, ExecV2Status, ExecV2Intent
- `src/unifocl/Services/ExecCommandRegistry.cs` — operation→risk マッピング + ProjectCommandRequestDto 変換
- `src/unifocl/Services/ExecApprovalService.cs` — approval ticket 発行・消費 (in-memory)
- `src/unifocl/Services/ExecOperationRouter.cs` — validation → approval gate → dispatch

**変更ファイル**:
- `src/unifocl/Services/DaemonControlService.cs`
  - `RunDaemonServiceAsync`: ExecCommandRegistry, ExecApprovalService, ExecOperationRouter をインスタンス化
  - `HandleDaemonRequestAsync`: 引数に `ExecOperationRouter execRouter` 追加
  - `/agent/exec` ハンドラー: JSON body に `operation` フィールドがあれば ExecV2 ルーター経由、なければ legacy (要 `UNIFOCL_LEGACY_EXEC=1`)

**動作確認済みフロー**:
1. `POST /agent/exec {"requestId":"...", "operation":"asset.rename", "args":{"assetPath":"...","newAssetPath":"..."}}`
   → `{"status":"ApprovalRequired", "approvalToken":"<token>", ...}`
2. 同エンドポイントに `"intent":{"approvalToken":"<token>"}` 付きで再送
   → `{"status":"Completed", ...}`

**対応 operation**:
- `asset.rename` (destructive_write) — 要承認
- `asset.remove` (destructive_write) — 要承認
- `asset.create_script` (safe_write) — session 未信頼時は要承認
- `asset.create` (safe_write) — 同上
- `build.run` (privileged_exec) — 要承認
- `approval.confirm` (safe_read) — pending ticket 消費して実行

---

### ✅ Sprint 2: 既存処理統合 + Session 導入
**ブランチ**: `feature/exec-v2-sprint2-session`
**コミット**: 最新

**完了タスク**:
1. ExecCommandRegistry に追加:
   - `build.exec` (privileged_exec), `upm.remove` (destructive_write)
   - `build.scenes.set` (safe_write), `hierarchy.snapshot` (safe_read)
   - `session.open / close / status` (safe_read)
2. `ExecSessionService.cs` 実装
   - `session.open(projectPath, runtimeType)` → sessionId 発行 (in-memory registry)
   - Trusted フラグで safe_write の承認要否を制御
3. `CliSessionState.SessionId` フィールド追加 + ResetToBoot でクリア
4. ExecOperationRouter: session.* のインライン処理 + IsTrusted で approval 制御
5. DaemonControlService: ExecSessionService をインスタンス化・注入

**未完了 (次スプリントへ)**:
- CLIDaemon.cs の `/project/command` Adapter 化 (CLIDaemon は Unity 側のため Sprint 3 以降)
- DaemonControlService の attach ロジック → session.open 置換

**新規ファイル**:
- `src/unifocl/Services/ExecSessionService.cs`

**変更ファイル**:
- `src/unifocl/Models/CliSessionState.cs` (SessionId 追加)
- `src/unifocl/Services/ExecCommandRegistry.cs` (operation 拡張)
- `src/unifocl/Services/ExecOperationRouter.cs` (session 注入・制御)
- `src/unifocl/Services/DaemonControlService.cs` (ExecSessionService 追加)

**キーファイル (次スプリント変更対象)**:
- `src/unifocl.unity/EditorScripts/CLIDaemon.cs`
- `src/unifocl/Models/CliSessionState.cs` (AttachedPort 維持 + SessionId 追加)
- `src/unifocl/Services/DaemonControlService.cs` (attach → session.open 移行部分)
- 新規: `src/unifocl/Services/ExecSessionService.cs`

---

### 🔲 Sprint 3: Transport 抽象化
**タスク**:
- `HierarchyDaemonClient.cs` 内の private `IProjectMutationTransport` を `src/unifocl/Services/Transport/` に昇格
- `IExecTransportServer.cs`, `IExecTransportClient.cs`, `IExecRequestContext.cs` 作成
- DaemonControlService / CLIDaemon の HttpListener を1層ラップ
- UDS (Unix Domain Socket) server/client を `Transport/Uds/` に追加

---

### 🔲 Sprint 4: Session 移行完了
- `CliSessionState.AttachedPort` deprecated 化、`SessionId` 主キー化
- orphan cleanup 実装

---

### 🔲 Sprint 5: HTTP Opt-in 化
- `--unsafe-http` フラグ以外では HttpListener 起動しない
- `UNIFOCL_LEGACY_EXEC=1` フラグ削除 (CommandText 実行コード除去)
- `/touch`, `/stop` を internal-only に縮退

---

## 重要な設計メモ

### ExecOperationRouter の dispatch 先
- `ExecCommandRegistry.TryBuildProjectRequest()` で ExecV2Request → ProjectCommandRequestDto に変換
- `ProjectDaemonBridge.TryHandle("PROJECT_CMD <json>")` に渡す (既存インフラ再利用)
- `MutationIntentFactory.EnsureProjectIntent()` で IntentEnvelope を自動生成 (DryRun フラグは ExecV2 側から上書き)

### 承認フロー
- ApprovalService は in-memory (プロセス再起動で消える) — Sprint 2 以降でディスク永続化検討
- token は requestId + operation にバインドされており、operation 不一致は拒否

### 既存コードとの互換
- `ProjectDaemonBridge` は CLI 側の stub bridge (Unity 未接続時用)
- 実際の Unity 接続時は `CLIDaemon` の `/project/command` または `/project/mutation/submit` 経由
- Sprint 2 で CLIDaemon 側にも ExecV2 adapter を追加する

### feature flag
- `UNIFOCL_LEGACY_EXEC=1` — 旧 CommandText フリーフォーム実行を許可
- Sprint 5 でこの flag ごと削除する

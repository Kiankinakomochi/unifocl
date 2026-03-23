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

### ✅ Sprint 3: Transport 抽象化
**ブランチ**: `feature/exec-v2-sprint3-transport`
**PR**: #126

**追加ファイル**:
- `src/unifocl/Services/Transport/IExecRequestContext.cs`
- `src/unifocl/Services/Transport/IExecTransportClient.cs`
- `src/unifocl/Services/Transport/IExecTransportServer.cs`
- `src/unifocl/Services/Transport/HttpExecRequestContext.cs`
- `src/unifocl/Services/Transport/HttpExecTransportServer.cs`
- `src/unifocl/Services/Transport/Uds/UdsExecRequestContext.cs`
- `src/unifocl/Services/Transport/Uds/UdsExecTransportServer.cs`

---

### ✅ Sprint 4: Session 移行完了
**ブランチ**: `feature/exec-v2-sprint4-session-migration`

**完了タスク**:
1. `CliSessionState.AttachedPort` に `[Obsolete]` 追加 — Sprint 5 で削除予定
2. `ExecSession` レコードに `Port` (int?) フィールド追加
3. `ExecSessionService` に port ベースのメソッド追加:
   - `OpenForPort(port, projectPath)` — port に紐づくセッションを開く (既存は置換)
   - `CloseByPort(port)` — port に紐づくセッションを閉じる
   - `GetByPort(port)` — port からセッションを検索
4. `ExecSessionService` にオーファンクリーンアップ実装:
   - `CleanupOrphans(TimeSpan maxIdle)` メソッド
   - バックグラウンドタイマー (5分間隔、30分アイドルで削除)
   - `IDisposable` 実装
5. `DaemonControlService` を更新:
   - `_sessionService` をフィールドに昇格 (旧: `RunDaemonServiceAsync` のローカル変数)
   - `SetAttachedPort(session, port, projectPath)` ヘルパー追加 — `AttachedPort` + `SessionId` を同期
   - `ClearAttachedPort(session)` ヘルパー追加 — `AttachedPort` + `SessionId` をクリア
   - 全 `session.AttachedPort = port` / `session.AttachedPort = null` をヘルパー経由に統一
   - `#pragma warning disable CS0618` を追加 (移行中ファイルのため)

**変更ファイル**:
- `src/unifocl/Models/CliSessionState.cs` (AttachedPort に `[Obsolete]`)
- `src/unifocl/Services/ExecSessionService.cs` (Port フィールド、port 系メソッド、orphan cleanup)
- `src/unifocl/Services/DaemonControlService.cs` (`_sessionService` フィールド化、sync helpers)

---

### ✅ Sprint 4.5: Sprint 3 残作業 — Transport Client 完成 (Sprint 5 前提条件)
**ブランチ**: `feature/exec-v2-sprint4-session-migration` (Sprint 4 コミットに追加)

**背景**: Sprint 3 で `IExecTransportClient` インターフェースは作成されたが、具体実装と
`HierarchyDaemonClient` の移行が未完了だった。Sprint 5 の HTTP Opt-in 化の前提として完了させる。

**完了タスク**:
1. `HttpExecTransportClient.cs` 追加 (`Transport/` フォルダ)
   - `IExecTransportClient` の具体実装
   - `HierarchyDaemonClient.ExecuteDurableMutationOverHttpAsync` に委譲
2. `HierarchyDaemonClient` 更新:
   - private `IProjectMutationTransport` / `HttpProjectMutationTransport` を削除
   - `internal static IExecTransportClient MutationTransport = new HttpExecTransportClient()` に置換
   - `ExecuteDurableMutationOverHttpAsync` を `internal static` に変更

**今後の展開**: Unity 側が UDS に対応したら `UdsExecTransportClient` を実装し、
`HierarchyDaemonClient.MutationTransport` を差し替えるだけで CLI→Unity の transport が切り替わる。

---

### ✅ Sprint 5: HTTP Opt-in 化
**ブランチ**: `feature/exec-v2-sprint5-http-optout`

**完了タスク**:
1. UDS を Windows でも有効化
   - `!OperatingSystem.IsWindows()` ガードを削除 (Windows 10 1803+ / .NET 5+ で UDS 動作確認済み)
   - Named Pipe 実装は不要と判断 (UDS が全プラットフォームで機能するため)
2. HTTP を `--unsafe-http` フラグ時のみ起動
   - `DaemonServiceOptions` に `bool UnsafeHttp = false` 追加
   - `TryParseDaemonServiceArgs` に `--unsafe-http` ケース追加
   - `RunDaemonServiceAsync`: `httpServer` を `options.UnsafeHttp` 時のみ生成・起動
   - UDS を起動の第一優先に変更 (ループ順序も UDS → HTTP)
3. `UNIFOCL_LEGACY_EXEC=1` フラグと CommandText 実行コードを削除
   - `/agent/exec` の legacy 分岐を完全除去
   - `operation` フィールドなしのリクエストは即 rejection
4. `/touch`, `/stop` を internal-only に縮退
   - `IExecRequestContext` に `bool IsInternal` プロパティ追加
   - `HttpExecRequestContext.IsInternal = false`
   - `UdsExecRequestContext.IsInternal = true`
   - `/touch`, `/stop` は `IsInternal = false` のリクエストに 403 を返す

**変更ファイル**:
- `src/unifocl/Models/CommandModels.cs` (`DaemonServiceOptions` に `UnsafeHttp`)
- `src/unifocl/Services/Transport/IExecRequestContext.cs` (`IsInternal` 追加)
- `src/unifocl/Services/Transport/HttpExecRequestContext.cs` (`IsInternal = false`)
- `src/unifocl/Services/Transport/Uds/UdsExecRequestContext.cs` (`IsInternal = true`)
- `src/unifocl/Services/DaemonControlService.cs` (全変更の適用先)

---

### ✅ Sprint 6: セキュリティ強化
**ブランチ**: `feature/exec-v2-sprint6-hardening`
**PR**: #129
**コミット**: `bfceb6c`

**完了タスク**:
1. UDS ソケットファイルの明示的 `chmod 0600`
   - `UdsExecTransportServer.Start()` で `File.SetUnixFileMode(_socketPath, UserRead | UserWrite)` を呼ぶ
   - Windows は RuntimeInformation でスキップ
2. `--unsafe-http` 時のトークン認証
   - 起動時に `Guid.NewGuid().ToString("N")` でトークン生成
   - `~/.unifocl-runtime/http-token-{port}.txt` に書き出し (chmod 0600)
   - `HttpExecTransportServer` に `requiredToken` を渡す
   - `AcceptAsync`: `X-Unifocl-Token` ヘッダー不一致は 401 で拒否してループ継続
   - シャットダウン時にトークンファイルを削除
3. HTTP リクエストボディサイズ制限 (1 MB)
   - `HttpExecRequestContext.ReadBodyAsync` に上限チェック追加
   - 超過時は `RequestTooLargeException` をスロー → `HandleDaemonRequestAsync` で 413 を返す
4. `ExecApprovalService` のディスク永続化
   - コンストラクタに `storePath?` 追加
   - `CreatePendingApproval` / `TryConsume` / `Reject` の都度 JSON フラッシュ
   - 起動時ロード (24h 超のエントリは破棄)
   - `Dispose()` でファイル削除
5. ビルドエラー修正 (Sprint 5 未解決)
   - `_sessionService` / `SetAttachedPort` / `ClearAttachedPort` を `static` に変更
   - `static RunDaemonServiceAsync` からの呼び出しエラーを解消

**変更ファイル**:
- `src/unifocl/Services/Transport/Uds/UdsExecTransportServer.cs`
- `src/unifocl/Services/Transport/HttpExecTransportServer.cs`
- `src/unifocl/Services/Transport/HttpExecRequestContext.cs` (`RequestTooLargeException` 追加含む)
- `src/unifocl/Services/ExecApprovalService.cs`
- `src/unifocl/Services/DaemonControlService.cs`

---

### ✅ Sprint 7: AttachedPort 削除 + CLIDaemon ExecV2 Adapter
**ブランチ**: `feature/exec-v2-sprint7-cleanup-adapter`
**PR**: (作成待ち)

**完了タスク**:
1. `CliSessionState.AttachedPort` の完全削除
   - `DaemonControlService` に `internal static int? GetPort(CliSessionState)` を追加 (`_sessionService` 経由)
   - `SetAttachedPort` / `ClearAttachedPort` を `internal static` に昇格
   - `CliSessionState.cs` から `AttachedPort` プロパティと `[Obsolete]` を削除
   - 影響 9 ファイル (BuildCommandService, InspectorModeService, ProjectLifecycleService 等) で呼び出し箇所を全て `DaemonControlService.GetPort(session)` に置換
2. CLIDaemon ExecV2 adapter
   - `CLIDaemon.cs` に `POST /agent/exec` エンドポイント追加
   - `ExecV2AdapterRequest` / `ExecV2AdapterArgs` を `DaemonBridgeModels.cs` に追加 (`[Serializable]`)
   - `HandleExecV2Async` で operation を switch し、`ProjectCommandRequest` JSON を構築して `DaemonProjectService.ExecuteAsync` に渡す
   - 対応 operation: `asset.rename/remove/create/create_script`, `build.run/exec/scenes.set`, `upm.remove`
   - レスポンス形式: `{"status":"Completed","requestId":"...","result":{...}}` or `{"status":"Failed","error":"..."}`

**変更ファイル**:
- `src/unifocl/Models/CliSessionState.cs`
- `src/unifocl/Services/DaemonControlService.cs`
- `src/unifocl/Services/AgenticStatePersistenceService.cs`
- `src/unifocl/Services/ProjectViewService.cs`
- `src/unifocl/Services/CliDumpService.cs`
- `src/unifocl/Services/CliOneShotExecutionService.cs`
- `src/unifocl/Services/HierarchyTui.cs`
- `src/unifocl/Services/BuildCommandService.cs`
- `src/unifocl/Services/InspectorModeService.cs`
- `src/unifocl/Services/ProjectLifecycleService.cs`
- `src/unifocl.unity/EditorScripts/CLIDaemon.cs`
- `src/unifocl.unity/EditorScripts/Models/DaemonBridgeModels.cs`

---

## 次エージェントへの引き継ぎ

**Sprint 1–7 完了済み。** ExecV2 のコア実装は全て完了。

残る検討事項 (優先度低):
- Unity 側 CLIDaemon が現在も HTTP のみ (CLIDaemon は Unity Editor に内蔵 → UDS は CLI 側との間)
- `ExecV2AdapterArgs.scenes` の JsonUtility デシリアライズ: `string[]` はサポートされているが、JSON の `args.scenes` が素の配列ではなく ExecV2AdapterArgs のフィールドとして届くことを前提としている。ExecOperationRouter 側のシリアライズが合っていることを E2E テストで確認推奨
- `build.scenes.set` のみ `args` がネストした配列を持つ — CLI 側の `ExecCommandRegistry` は `JsonElement` の raw text を使っているが CLIDaemon 側は `string[]` でデシリアライズするため、フォーマットが合っているか確認

worktree の作成方法 (次 Sprint がある場合):
```
git -C /Users/makinokinichianju/Documents/GitHub/unifocl worktree add \
  /Users/makinokinichianju/Documents/GitHub/unifocl-exec-v2 \
  -b feature/exec-v2-sprint8-<short> \
  origin/feature/exec-v2-sprint7-cleanup-adapter
```

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

# Vive Ultimate Tracker Without HMD (Unity) - 日本語README

このリポジトリは、Vive Ultimate Tracker ドングルから直接ポーズを取得し、Unity シーンへ反映する最小構成の実装です。HMD なしでトラッカーの位置・回転を扱えます。

主な構成
- Runtime/Scripts/UltimateTrackerReceiver.cs
  - ドングル接続/切断、入力レポートの読み取り、ポーズ解析
  - Rawポーズを `OnTrackerPose` で通知し、Unity座標系（軸反転 + グローバルオフセット適用後）のポーズを `SimpleTrackerState.UnityPositionAdjusted/UnityRotationAdjusted` に格納
  - グローバルなオフセット行列（`Matrix4x4`）を 1 つ保持し、全トラッカーに対して適用
- Runtime/Scripts/Protocol/TrackerReportParser.cs
  - ドングルからの入力を解析し、Tracker の状態 (`SimpleTrackerState`) を更新
- Runtime/Scripts/UltimateTrackerPoseApplier.cs
  - `UltimateTrackerReceiver` の更新済みポーズを Transform に適用（最小）

イベント
- OnTrackerPose: ドングルから受信して解析した直後のポーズを通知します（トラッカーの生データベース）。
- OnTrackerConnected / OnTrackerDisconnected: トラッカーの接続/切断
- OnRfStatus / OnVendorStatus / OnPairEvent / OnAck: ステータス系イベント

Unity座標系への変換
- 受信ポーズ（`SimpleTrackerState.Position/Rotation`）に対して、次を適用して Unity座標系の最終値を生成します:
  1) 軸反転: posX / yaw / roll の反転（X位置の符号、Y/Zの回転角符号）
  2) グローバルオフセット: `Matrix4x4` を左乗（`final = Offset * TRS(raw)`）
- 変換後は以下に格納されます:
  - `SimpleTrackerState.UnityPosition` / `UnityRotation`（軸反転まで適用）
  - `SimpleTrackerState.UnityPositionAdjusted` / `UnityRotationAdjusted`（軸反転 + オフセット適用後）

オフセット操作（全トラッカーに適用）
- SetGlobalUnityOffset(Matrix4x4 offset)
- SetGlobalUnityOffset(Vector3 positionOffset, Quaternion rotationOffset)
- ClearGlobalUnityOffset() / ClearTracker()（identity に戻す）

使い方（クイック）
1) シーンに `UltimateTrackerReceiver` を配置し、Play で自動接続（`autoConnectOnStart`）
2) `UltimateTrackerPoseApplier` を任意の Transform にアタッチ
3) `UltimateTrackerReceiver` の `OnTrackerPose` で raw、`UnityPositionAdjusted/UnityRotationAdjusted` で最終Poseを参照

注意
- 軸反転はオイラー角ベースで実装されています。厳密な行列ベースが必要であれば拡張可能です。
- 行列オフセットは 1 つのみ保持され、全トラッカーに適用されます。

## 参考リポジトリ
本実装は、以下の素晴らしいリバースエンジニアリング/検証の成果に強く影響を受けています。

- https://github.com/mgschwan/ViveUltimateTrackerMobile （ViveUltimateTrackerMobile）

同リポジトリは、Vive Ultimate Tracker ドングルに対してモバイル/Android（Java/Kotlin + USB HID ブリッジ）の経路で、ペアリング、HID Feature レポート、Pose パケットの解読などを幅広く試行しています。本 Unity 実装は独立に作成したものですが、パケット構造の考察や「HMDなしでトラッカーを動かす」という発想はこの取り組みと整合的です。派生ツールを作る場合は、元の成果にもクレジットをお願いします。

本リポジトリの相違点:
- デバイス個別ではなく単一の `Matrix4x4` グローバルオフセットを採用
- Unity の最小 MonoBehaviour 構成（`UltimateTrackerReceiver`, `UltimateTrackerPoseApplier`）
- 軸反転（posX/yaw/roll）とキャリブレーションフローの簡素化

改善点やプロトコル知見が得られた場合は、元の参考リポジトリにもぜひフィードバックをご検討ください。

ライセンス
- このリポジトリに付属の LICENSE を参照してください。

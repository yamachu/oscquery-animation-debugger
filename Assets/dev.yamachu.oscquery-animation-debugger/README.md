# OSCQuery Animation Debugger 導入ガイド

このドキュメントは、VPM (VRChat Package Manager) を使ってプロジェクトへ導入し、使い始めるための手順です。

## これは何をするツール？

OSCQuery Animation Debugger は、OSCQuery 経由で受け取った値をアバターのパラメーターへ反映し、デバッグしやすくするためのコンポーネントです。

主に次の環境で利用できます。

- Lyuma Av3Emulator
- VRC Gesture Manager
- RuntimeAnimatorController を持つ Animator
- ndmf / Modular Avatar（Apply on Play を使う構成）

## 導入手順（推奨: VPM）

1. VCC を起動します。
2. Settings -> Packages -> Add Repository を開きます。
3. 次の VPM リポジトリ URL を追加します。
	- [VPM_REPOSITORY_URL_PLACEHOLDER]
4. 対象プロジェクトの Manage Project を開きます。
5. Packages 一覧から OSCQuery Animation Debugger を探して Install します。
6. Resolve / Apply が表示された場合は実行します。
7. Console にコンパイルエラーが出ていないことを確認します。

## 導入手順（補助: unitypackage 配布時）

unitypackage 版を配布する場合は、依存解決を手動で行う必要があります。

1. unitypackage をプロジェクトに Import します。
2. NuGetForUnity が未導入なら、先に導入します。
3. Project ウィンドウで Assets/dev.yamachu.oscquery-animation-debugger/packages.config を確認します。
4. NuGetForUnity で packages.config を Restore し、依存パッケージを復元します。
5. Console にコンパイルエラーが出ていないことを確認します。

依存パッケージには少なくとも次が含まれます。

- OscCore 1.0.5
- VRChat.OSCQuery 0.0.7

## 使い方（最短）

1. デバッグ対象アバターのルート、または制御対象に近い GameObject に OscQueryAnimationDebugger を追加します。
2. Play します。
3. Inspector の Parameter Driver Mode を必要に応じて選択します。

## OSC Tracker の設定

標準の `/tracking/trackers/{1..8|head}/{position|rotation}` を受信し、任意の Transform へ生の姿勢を反映できます。

1. Inspector の Enable Trackers を有効にします。
2. Tracker Bindings に要素を追加し、送信側の slot、対象の Target Transform、Position / Rotation の適用可否を設定します。
3. 必要なら Tracker Reference Transform を指定します。未指定の場合は、このコンポーネントが属する `transform.root` を基準にします。
4. 送信アプリから Debugger の OSC UDP ポートへ tracker packet を送ります。VRChatにも同じ値を送る場合は、送信側で宛先を分岐してください。

Target には Humanoid Bone に限らず、Hierarchy 上の任意の Transform をドラッグ＆ドロップできます。slot 1～8 は腰・足などの部位を固定的に表すものではありません。送信側の割り当てに合わせて対応する Transform を選んでください。重複した slot は最初の割り当てだけが使われます。

Position はメートル単位の左手系座標として `Reference.TransformPoint(value)` で Unity world 座標へ変換します。Rotation は degree の Euler 角として `Reference.rotation * Quaternion.Euler(x, y, z)` で Unity world 回転へ変換します。Position と Rotation は独立して適用でき、受信していない側の姿勢は維持されます。Stale Timeout を過ぎても最終姿勢へ戻し処理は行いません。

`head` は VRChat では tracking-space alignment に使われる特殊な入力です。このツールでは、割り当てた Transform へ head の raw pose をそのまま表示します。VRChat の全身 IK や頭部補正、Eye Tracking、スムージングは再現しないため、VRChat 内の最終 Bone 姿勢とは一致しない場合があります。

## Parameter Driver Mode の選び方

- Auto（既定）
	- Gesture Manager → Animator → Av3Emulator → Custom Components の順で試します。
	- 迷ったらまずこれを使ってください。
- Av3Emulator
	- Animator → Av3Emulator → Custom Components の順で試します。
	- Av3Emulator 中心で使う場合に向いています。
- GestureManager
	- Gesture Manager → Animator → Custom Components の順で試します。
	- Gesture Manager での編集反映を優先したい場合に向いています。
- AnimatorOnly
	- Animator → Custom Components のみ使います。
	- ほかのツール連携を避けたい場合に使います。

複数ツールが同時に有効な場合は、選択モードの優先順で評価され、書き込みに成功したドライバーが使われます。

## 動作確認チェックリスト

- OscQueryAnimationDebugger コンポーネントを追加できる
- Missing Script 警告が出ない
- OscCore / VRC.OSCQuery 関連のコンパイルエラーがない

## うまく動かないとき

- NuGetForUnity で packages.config の Restore をやり直す
- Parameter Driver Mode を Auto に戻して再確認する
- Gesture Manager や Av3Emulator を使う場合は、それぞれが正常に動作しているか確認する
- プロジェクトを再読み込みしてコンパイルを待つ

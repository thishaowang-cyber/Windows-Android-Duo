package com.example.laptophinge

/**
 * UI 文案辅助。
 *
 * 单独抽出来是为了让「状态怎么描述」这件事只有一处定义——
 * 将来接 BLE / 网络把状态发给电脑端时，两端必须用同一套判据，
 * 否则会出现「手机显示可跟踪、电脑认为不可跟踪」的错位。
 */

/** 采样与跟踪状态的描述。 */
object SensorStatusText {

    fun describe(state: HingeUiState): String {
        val s = state.status ?: return "初始化中"
        if (s.sourceName == null) return "没有可用的重力源"
        if (!state.running) return "已暂停"
        return when {
            state.observability < 0.3 -> "低可观测（接近合盖/摊平）"
            state.stationary -> "静止"
            else -> "跟踪中"
        }
    }
}

/** 45°/50° 迟滞锁定状态的描述（阈值与 [com.example.laptophinge.sensor.LockStateMachine] 一致）。 */
object LockStateMachineText {

    fun describe(state: HingeUiState): String = when {
        !state.running -> "—"
        state.locked -> "≤45° 锁定"
        else -> "≥50° 解锁"
    }
}

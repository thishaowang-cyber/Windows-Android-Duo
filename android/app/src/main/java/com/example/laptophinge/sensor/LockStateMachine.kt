package com.example.laptophinge.sensor

/**
 * 45° / 50° 迟滞锁定状态机（规格书第八节）。
 *
 *     angle <= 45°  →  LOCKED
 *     angle >= 50°  →  TRACKING（解锁）
 *
 * 两个阈值之间的 5° 死区是**迟滞**：角度在 45° 附近抖动时状态不会来回翻。
 * 这段逻辑手机端和将来的 Windows 端必须完全一致，否则会出现「手机显示锁定、
 * 电脑却认为没锁」的错位，所以阈值做成可配置，默认值就是规格书里的 45/50。
 *
 * 这个类不依赖 Android，可以被单元测试完整覆盖（含边界值）。
 */
class LockStateMachine(
    val lockThresholdDeg: Double = 45.0,
    val unlockThresholdDeg: Double = 50.0,
) {
    enum class State { UNKNOWN, LOCKED, TRACKING }

    var state: State = State.UNKNOWN
        private set

    /** 上次状态翻转的时刻（毫秒，调用方给的时间基），用于 UI 展示冷却时间。 */
    var lastTransitionElapsedMs: Long = 0L
        private set

    init {
        require(unlockThresholdDeg > lockThresholdDeg) {
            "解锁阈值 ($unlockThresholdDeg) 必须大于锁定阈值 ($lockThresholdDeg)，否则迟滞失效"
        }
    }

    /**
     * 送入一个角度，返回更新后的状态。
     *
     * 初始状态是 UNKNOWN：第一次送入角度时按当前值直接决定初始状态，
     * 不走迟滞（否则必须先小于 45° 才会锁，冷启动就错了）。
     */
    fun update(angleDeg: Double, elapsedMs: Long = 0L): State {
        if (!angleDeg.isFinite()) return state

        val previous = state
        state = when (previous) {
            // 冷启动：直接按当前角度定位，不制造假的状态跳变
            State.UNKNOWN -> if (angleDeg <= lockThresholdDeg) State.LOCKED else State.TRACKING
            // 已锁定：只有升到解锁阈值之上才解除
            State.LOCKED -> if (angleDeg >= unlockThresholdDeg) State.TRACKING else State.LOCKED
            // 已解锁：只有降回锁定阈值之下才重新锁定
            State.TRACKING -> if (angleDeg <= lockThresholdDeg) State.LOCKED else State.TRACKING
        }

        if (state != previous) lastTransitionElapsedMs = elapsedMs
        return state
    }

    fun reset() {
        state = State.UNKNOWN
        lastTransitionElapsedMs = 0L
    }

    val isLocked: Boolean get() = state == State.LOCKED
}

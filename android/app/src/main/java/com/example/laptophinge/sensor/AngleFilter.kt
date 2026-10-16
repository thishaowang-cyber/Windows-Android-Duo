package com.example.laptophinge.sensor

import kotlin.math.PI
import kotlin.math.abs

/**
 * 角度滤波器（规格书第七节）。
 *
 * 目标：把 71/75/70 这种 ±2° 的抖动压到 0.1° 量级，同时**不能引入明显延迟**——
 * 屏幕上看到的角度要比手慢半拍就废了。所以提供两种模式：
 *
 *  - [Mode.EMA]：固定系数一阶低通。实现最简单，静止时最平滑，
 *    但快速开合时会明显滞后（因为它对大误差和小误差一视同仁）。
 *  - [Mode.ONE_EURO]：1€ 滤波器。系数随角速度自适应——
 *    手停着时强滤波（几乎不动），快速开合时弱滤波（紧跟真实角度）。
 *    这是 VR/AR 里的标准做法，代价只是多两行。
 *
 * 两种模式都只处理标量角度，与传感器解耦，因此可被纯 JVM 单元测试直接覆盖。
 */
class AngleFilter(
    var mode: Mode = Mode.ONE_EURO,
    /** EMA 系数；越小越平滑、延迟越大。 */
    var alpha: Double = 0.25,
    /** 1€ 滤波器的截止频率（Hz）：静止时允许通过的噪声带宽。 */
    var minCutoffHz: Double = 1.2,
    /** 速度系数：越大则快速运动时越信任新样本。 */
    var beta: Double = 0.05,
    /** 速度估计自身的低通系数。 */
    var derivativeAlpha: Double = 0.2,
) {
    enum class Mode { EMA, ONE_EURO, OFF }

    private var initialized = false
    private var value = 0.0
    private var derivative = 0.0
    private var lastTimestampNs = 0L

    /** 最近一次滤波后的角度（度）。 */
    val current: Double get() = value

    /** 最近一次估计的角速度（度/秒），带符号。 */
    val velocityDegPerSec: Double get() = derivative

    fun reset(initialValue: Double = 0.0) {
        value = initialValue
        derivative = 0.0
        initialized = true
        lastTimestampNs = 0L
    }

    /**
     * 送入一个新样本。
     *
     * @param rawDeg 解算出的原始角度
     * @param timestampNs 传感器事件时间戳（SystemClock.elapsedRealtimeNanos 时基），
     *        用传感器自己的时间戳而不是 System.nanoTime()，可避免抖动
     */
    fun update(rawDeg: Double, timestampNs: Long): Double {
        if (!rawDeg.isFinite()) return value

        // 第一帧直接接受，不做滤波（否则会从 0 慢慢爬上来）
        if (!initialized) {
            reset(rawDeg)
            lastTimestampNs = timestampNs
            return value
        }

        // 时间步长：夹在 [1ms, 100ms]，防止卡顿或时间戳异常导致系数爆炸
        val dtSec = if (lastTimestampNs == 0L) {
            0.01
        } else {
            ((timestampNs - lastTimestampNs).coerceIn(1_000_000L, 100_000_000L)) / 1e9
        }
        lastTimestampNs = timestampNs

        when (mode) {
            Mode.OFF -> value = rawDeg
            Mode.EMA -> value += alpha.coerceIn(0.0, 1.0) * (rawDeg - value)
            Mode.ONE_EURO -> {
                // 1) 先估计角速度（对原始差分做低通，否则噪声会污染自适应系数）
                val rawDerivative = (rawDeg - value) / dtSec
                derivative += derivativeAlpha.coerceIn(0.0, 1.0) * (rawDerivative - derivative)

                // 2) 截止频率随速度上升：fc = minCutoff + beta · |v|
                val cutoff = minCutoffHz + beta * abs(derivative)
                val tau = 1.0 / (2.0 * PI * cutoff)
                var a = dtSec / (dtSec + tau)
                a = a.coerceIn(0.0, 1.0)

                // 3) 一阶低通
                value += a * (rawDeg - value)
            }
        }
        return value
    }
}

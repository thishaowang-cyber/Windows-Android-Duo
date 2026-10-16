package com.example.laptophinge.sensor

import kotlin.math.abs
import kotlin.math.sqrt

/**
 * 三维向量工具。
 *
 * 本项目里所有向量都按 Android 传感器坐标系（设备坐标系）解释：
 *  - x 轴：屏幕右侧
 *  - y 轴：屏幕上方
 *  - z 轴：垂直于屏幕向外（指向用户）
 *
 * 为了能被纯 JVM 单元测试直接覆盖，这里不依赖任何 Android 类，
 * 用 DoubleArray(3) 而不是 android.graphics.Vector3f。
 */
object Vec3 {

    fun of(x: Double, y: Double, z: Double) = doubleArrayOf(x, y, z)

    fun dot(a: DoubleArray, b: DoubleArray): Double =
        a[0] * b[0] + a[1] * b[1] + a[2] * b[2]

    fun cross(a: DoubleArray, b: DoubleArray): DoubleArray = doubleArrayOf(
        a[1] * b[2] - a[2] * b[1],
        a[2] * b[0] - a[0] * b[2],
        a[0] * b[1] - a[1] * b[0],
    )

    fun norm(a: DoubleArray): Double = sqrt(dot(a, a))

    /** 归一化；零向量返回 null，交由调用方决定如何处理。 */
    fun normalize(a: DoubleArray): DoubleArray? {
        val n = norm(a)
        if (n < 1e-12) return null
        return doubleArrayOf(a[0] / n, a[1] / n, a[2] / n)
    }

    fun scale(a: DoubleArray, s: Double) = doubleArrayOf(a[0] * s, a[1] * s, a[2] * s)

    fun plus(a: DoubleArray, b: DoubleArray) =
        doubleArrayOf(a[0] + b[0], a[1] + b[1], a[2] + b[2])

    /**
     * 把向量整体取反，使它与 [reference] 同向。
     *
     * 铰链轴是一个「方向」而不是「有向线段」：陀螺仪积分出来的轴可能整体反号，
     * 角度绝对值不受影响，但正负号会翻转。统一到同一半球后结果才可复现。
     */
    fun alignTo(a: DoubleArray, reference: DoubleArray): DoubleArray =
        if (dot(a, reference) < 0.0) scale(a, -1.0) else a

    fun isFinite(a: DoubleArray): Boolean =
        a.size == 3 && a.all { it.isFinite() } && abs(norm(a)) > 1e-12
}

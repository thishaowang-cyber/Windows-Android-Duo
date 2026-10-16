package com.example.laptophinge.sensor

import kotlin.math.acos
import kotlin.math.atan2
import kotlin.math.cos
import kotlin.math.sin
import kotlin.math.sqrt

/**
 * 单位四元数，存储顺序为 (w, x, y, z)，w 为标量部分。
 *
 * 语义与 Android `SensorManager.getQuaternionFromVector()` 一致：
 * 四元数 q 表示把**设备坐标系**中的向量旋转到**世界坐标系**（东-北-天）的旋转。
 *
 * 因此对设备坐标系中的向量 v，它在世界系中的表示为：v_world = R(q) · v_device。
 *
 * 所有运算用 Double 进行，避免 Float 在连续积分/比较中累积误差。
 */
data class Quat(
    val w: Double,
    val x: Double,
    val y: Double,
    val z: Double,
) {
    companion object {
        val IDENTITY = Quat(1.0, 0.0, 0.0, 0.0)

        /**
         * 从 Android 的 4 元素旋转向量构造。
         *
         * TYPE_ROTATION_VECTOR / TYPE_GAME_ROTATION_VECTOR 的 values 就是
         * 单位四元数的 (x, y, z, w) 排列，个别实现归一化不严格，这里重新归一化。
         */
        fun fromRotationVector(values: FloatArray): Quat? {
            if (values.size < 4) return null
            val q = Quat(
                w = values[3].toDouble(),
                x = values[0].toDouble(),
                y = values[1].toDouble(),
                z = values[2].toDouble(),
            )
            return q.normalizedOrNull()
        }

        /** 绕单位轴 [axis] 旋转 [angleRad]。 */
        fun fromAxisAngle(axis: DoubleArray, angleRad: Double): Quat {
            val unit = Vec3.normalize(axis) ?: return IDENTITY
            val half = angleRad / 2.0
            val s = sin(half)
            return Quat(cos(half), unit[0] * s, unit[1] * s, unit[2] * s)
        }
    }

    fun vector(): DoubleArray = doubleArrayOf(x, y, z)

    fun norm(): Double = sqrt(w * w + x * x + y * y + z * z)

    fun normalizedOrNull(): Quat? {
        val n = norm()
        if (n < 1e-12 || !n.isFinite()) return null
        return Quat(w / n, x / n, y / n, z / n)
    }

    /** 共轭 = 逆（单位四元数前提下）。 */
    fun conjugate(): Quat = Quat(w, -x, -y, -z)

    /**
     * 汉密尔顿积。`this * other` 表示「先施加 other，再施加 this」。
     *
     * 注意：**不要**把这里的顺序套到 Android 的 `SensorManager.getOrientation`
     * 那条链路上——本项目只在同一个参考系（世界系）内做组合，见 QuatOps.relative。
     */
    operator fun times(o: Quat): Quat = Quat(
        w = w * o.w - x * o.x - y * o.y - z * o.z,
        x = w * o.x + x * o.w + y * o.z - z * o.y,
        y = w * o.y - x * o.z + y * o.w + z * o.x,
        z = w * o.z + x * o.y - y * o.x + z * o.w,
    )

    /** 用本四元数旋转设备系向量：v_world = R(q) · v_device。 */
    fun rotate(v: DoubleArray): DoubleArray {
        val o = Quat(0.0, v[0], v[1], v[2])
        val r = this * o * conjugate()
        return doubleArrayOf(r.x, r.y, r.z)
    }

    /** 旋转矩阵，行优先 3x3，与 Android `getRotationMatrixFromVector` 的输出一致。 */
    fun toRotationMatrix(): DoubleArray {
        val xx = x * x; val yy = y * y; val zz = z * z
        val xy = x * y; val xz = x * z; val yz = y * z
        val wx = w * x; val wy = w * y; val wz = w * z
        return doubleArrayOf(
            1.0 - 2.0 * (yy + zz), 2.0 * (xy - wz), 2.0 * (xz + wy),
            2.0 * (xy + wz), 1.0 - 2.0 * (xx + zz), 2.0 * (yz - wx),
            2.0 * (xz - wy), 2.0 * (yz + wx), 1.0 - 2.0 * (xx + yy),
        )
    }

    /**
     * 等价于 `SensorManager.getOrientation()` 的输出，单位弧度：
     *  - azimuth (yaw)  ∈ (-π, π]，绕世界 Z 轴（重力反方向）
     *  - pitch          ∈ [-π/2, π/2]，设备 +y 轴相对水平面的俯仰
     *  - roll           ∈ (-π, π]，设备绕自身 +y 轴的滚动
     *
     * 公式直接对照 Android 官方实现（基于 R = q 的旋转矩阵）：
     *   azimuth = atan2(R[1], R[4]),  pitch = asin(-R[7]),  roll = atan2(-R[6], R[8])
     */
    fun toEulerRadians(): DoubleArray {
        val r = toRotationMatrix()
        val azimuth = atan2(r[1], r[4])
        val pitch = atan2(-r[7], sqrt(r[6] * r[6] + r[8] * r[8]))
        val roll = atan2(-r[6], r[8])
        return doubleArrayOf(azimuth, pitch, roll)
    }

    /** 两个姿态之间的夹角（旋转测地距离），用于自检。 */
    fun angleTo(o: Quat): Double {
        val d = (this * o.conjugate()).w
        return 2.0 * acos(d.coerceIn(-1.0, 1.0))
    }
}

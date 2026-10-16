package com.example.laptophinge.sensor

import kotlin.math.abs
import kotlin.math.sqrt

/**
 * 铰链旋转轴的几何估计。
 *
 * 为什么不能直接写死某个轴：手机可以横着、竖着、斜着贴在 A 面，
 * 铰链轴在设备坐标系里的方向是完全未知的，必须测出来。
 *
 * 估计方法：把所有旋转增量（角速度向量）当成一簇样本，求它们的**主方向**。
 * 笔记本开合时，屏幕绕一条固定轴转动，因此全部角速度向量都近似平行于该轴，
 * 协方差矩阵的最大特征向量即为铰链轴（带权重时按 |ω|² 加权，抑制静止噪声）。
 */
object AxisEstimator {

    /** 样本少于该数量时不出结论，避免用两三个点硬拟合。 */
    const val MIN_SAMPLES = 30

    /** 累计转角（度）下限：开合幅度太小则主方向不可信。 */
    const val MIN_TOTAL_ROTATION_DEG = 25.0

    /** 主特征值占比下限：单一轴运动的判据，双轴乱动（比如手晃手机）会被拒绝。 */
    const val MIN_AXIS_CONFIDENCE = 0.85

    /** 单样本角速度下限（rad/s），低于此视为静止，不计入统计。 */
    private const val MIN_SPEED = 0.15

    data class Result(
        /** 设备系下的单位铰链轴。 */
        val axis: DoubleArray,
        /** 主特征值 / 全部特征值之和，越接近 1 说明运动越单一。 */
        val confidence: Double,
        /** 参与统计的样本数。 */
        val sampleCount: Int,
        /** 累计转角（度）。 */
        val totalRotationDeg: Double,
    )

    /** 增量式累加器：把角速度样本喂进来，最后一次性求解。 */
    class Accumulator {
        // 3x3 对称协方差矩阵的 6 个独立分量
        private var cxx = 0.0; private var cyy = 0.0; private var czz = 0.0
        private var cxy = 0.0; private var cxz = 0.0; private var cyz = 0.0
        private var wsumX = 0.0; private var wsumY = 0.0; private var wsumZ = 0.0
        private var count = 0
        private var totalAngle = 0.0

        val sampleCount: Int get() = count

        fun reset() {
            cxx = 0.0; cyy = 0.0; czz = 0.0
            cxy = 0.0; cxz = 0.0; cyz = 0.0
            wsumX = 0.0; wsumY = 0.0; wsumZ = 0.0
            count = 0; totalAngle = 0.0
        }

        /**
         * 加入一个陀螺仪样本。
         *
         * @param omega 设备系角速度 (rad/s)
         * @param dtSeconds 该样本代表的时长，用于累计转角
         */
        fun add(omega: DoubleArray, dtSeconds: Double) {
            if (!Vec3.isFinite(omega)) return
            val speed = Vec3.norm(omega)
            if (speed < MIN_SPEED) return
            // |ω|² 加权：转得快的样本更接近纯铰链运动，权重更高
            val w = speed * speed
            cxx += w * omega[0] * omega[0]
            cyy += w * omega[1] * omega[1]
            czz += w * omega[2] * omega[2]
            cxy += w * omega[0] * omega[1]
            cxz += w * omega[0] * omega[2]
            cyz += w * omega[1] * omega[2]
            wsumX += omega[0]; wsumY += omega[1]; wsumZ += omega[2]
            count++
            totalAngle += Math.toDegrees(speed * dtSeconds.coerceIn(0.0, 0.1))
        }

        fun estimate(): Result? {
            if (count < MIN_SAMPLES) return null
            if (totalAngle < MIN_TOTAL_ROTATION_DEG) return null
            if (cxx + cyy + czz < 1e-18) return null

            val axis = principalAxisOfSymmetric3x3(cxx, cyy, czz, cxy, cxz, cyz) ?: return null

            // 用平均角速度方向定符号，避免轴整体反号
            val mean = doubleArrayOf(wsumX, wsumY, wsumZ)
            val aligned = Vec3.alignTo(axis.vector, mean)

            return Result(
                axis = aligned,
                confidence = axis.dominance,
                sampleCount = count,
                totalRotationDeg = totalAngle,
            )
        }
    }

    internal data class Eigen(val vector: DoubleArray, val dominance: Double)

    /**
     * 3x3 实对称矩阵的主特征向量。
     *
     * 用幂迭代而不是解析三次方程：实现短、无分支判断、对本用途足够精确，
     * 且天然给出「主特征值占比」作为置信度。矩阵已按迹归一化，因此特征值之和为 1。
     */
    internal fun principalAxisOfSymmetric3x3(
        cxx: Double, cyy: Double, czz: Double,
        cxy: Double, cxz: Double, cyz: Double,
    ): Eigen? {
        val trace = cxx + cyy + czz
        if (trace < 1e-18) return null
        // 归一化，使特征值之和为 1 → dominance 直接可解释
        val m = doubleArrayOf(
            cxx / trace, cxy / trace, cxz / trace,
            cxy / trace, cyy / trace, cyz / trace,
            cxz / trace, cyz / trace, czz / trace,
        )

        var v = doubleArrayOf(1.0, 1.0, 1.0)
        // 找一个与初始向量不平行的更好起点，避免幂迭代收敛到非主特征向量
        if (abs(m[0] - m[4]) < 1e-12 && abs(m[4] - m[8]) < 1e-12) {
            v = doubleArrayOf(1.0, 0.0, 0.0)
        }

        var lambda = 0.0
        repeat(200) {
            val nv = doubleArrayOf(
                m[0] * v[0] + m[1] * v[1] + m[2] * v[2],
                m[3] * v[0] + m[4] * v[1] + m[5] * v[2],
                m[6] * v[0] + m[7] * v[1] + m[8] * v[2],
            )
            val n = Vec3.norm(nv)
            if (n < 1e-18) return null
            v = doubleArrayOf(nv[0] / n, nv[1] / n, nv[2] / n)
            lambda = n
        }

        // 幂迭代收敛到的是 |λ| 最大的特征向量；对称矩阵特征值非负（协方差半正定），
        // 所以 lambda = 主特征值（已按迹归一化，范围 [1/3, 1]）
        return Eigen(v, lambda.coerceIn(0.0, 1.0))
    }

    /** 独立复核用：把 3x3 对称矩阵的三个特征值算出来（解析法，仅测试使用）。 */
    internal fun eigenValuesSymmetric3x3(
        cxx: Double, cyy: Double, czz: Double,
        cxy: Double, cxz: Double, cyz: Double,
    ): DoubleArray {
        val p1 = cxy * cxy + cxz * cxz + cyz * cyz
        val q = (cxx + cyy + czz) / 3.0
        val p2 = (cxx - q) * (cxx - q) + (cyy - q) * (cyy - q) + (czz - q) * (czz - q) + 2.0 * p1
        val p = sqrt(p2 / 6.0)
        if (p < 1e-18) return doubleArrayOf(q, q, q)
        val b = doubleArrayOf(
            (cxx - q) / p, cxy / p, cxz / p,
            cxy / p, (cyy - q) / p, cyz / p,
            cxz / p, cyz / p, (czz - q) / p,
        )
        val detB = b[0] * (b[4] * b[8] - b[5] * b[7]) -
            b[1] * (b[3] * b[8] - b[5] * b[6]) +
            b[2] * (b[3] * b[7] - b[4] * b[6])
        val r = (detB / 2.0).coerceIn(-1.0, 1.0)
        val phi = Math.acos(r) / 3.0
        val e1 = q + 2.0 * p * Math.cos(phi)
        val e3 = q + 2.0 * p * Math.cos(phi + 2.0 * Math.PI / 3.0)
        val e2 = 3.0 * q - e1 - e3
        return doubleArrayOf(e1, e2, e3).sortedArray()
    }
}

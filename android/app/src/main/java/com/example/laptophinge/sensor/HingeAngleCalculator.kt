package com.example.laptophinge.sensor

import kotlin.math.abs
import kotlin.math.atan2

/**
 * 从「相对姿态」提取笔记本开合角度。
 *
 * 数学链路（对应规格书第四、五、六节）：
 *
 *     q_rel = q_now⁻¹ ⊗ q_ref                    // 设备系中的相对旋转
 *     raw   = 2 · atan2( q_rel.xyz · axis , q_rel.w )   // 绕铰链轴扫过的有符号角
 *
 * 第二式的几何含义：把相对旋转写成轴角形式，投影到估计出的铰链轴上。
 * 它天然给出 [−180°, 180°] 的连续角度，且对「手机怎么贴」不敏感——
 * 因为 [axis] 就是从这个设备自己的开合动作里测出来的，而不是硬编码的 x/y/z。
 *
 * 映射到物理角度用 [Mapping]（两点线性标定），不做任何硬编码假设：
 * 0° = 完全合盖、90° = 屏幕垂直、180° = 完全翻平，由用户在标定流程里提供两个参考点。
 */
class HingeAngleCalculator {

    /**
     * 两点线性标定：把传感器原始角映射到物理开合角。
     *
     * 用了两个参考点而不是「斜率 + 截距」，因为参考点才是用户真正能给的东西：
     * 合上盖子时点一下、立成 90° 时点一下，映射自动解出来——
     * 手机装歪、装反、装斜都不需要额外参数。
     */
    data class Mapping(
        /** 合盖状态下测得的原始传感器角（度）。 */
        val rawAtClosedDeg: Double,
        /** 屏幕垂直状态下测得的原始传感器角（度）。 */
        val rawAtVerticalDeg: Double,
        /**
         * 有效原始角跨度下限（度）。
         * 两个参考点太接近时斜率会被噪声放大，此时拒绝标定。
         */
        val minSpanDeg: Double = 20.0,
    ) {
        /** 每 1° 物理角度对应多少原始角。可能为负（手机装反时）。 */
        val scale: Double = (rawAtVerticalDeg - rawAtClosedDeg) / 90.0

        val isValid: Boolean = abs(rawAtVerticalDeg - rawAtClosedDeg) >= minSpanDeg

        /** 原始角 → 物理角，输出钳制在 [0, 180]。 */
        fun toPhysicalDegrees(rawDeg: Double): Double {
            if (!isValid) return rawDeg
            if (abs(scale) < 1e-9) return rawDeg
            val physical = (rawDeg - rawAtClosedDeg) / scale
            return physical.coerceIn(0.0, 180.0)
        }
    }

    /** 一次解算的完整结果。 */
    data class Result(
        /** 原始相对角（度），带符号，未做物理映射，范围 (−180, 180]。 */
        val rawAngleDeg: Double,
        /** 映射后的物理开合角（度），无映射时为 |rawAngleDeg|。 */
        val hingeAngleDeg: Double,
        /** 铰链轴（设备系单位向量）。 */
        val axis: DoubleArray,
        /** 轴估计置信度 0~1。 */
        val axisConfidence: Double,
        /** 轴是否已经历过自动标定。 */
        val axisCalibrated: Boolean,
        /** 相对姿态是否有效（校准参考存在且数值合法）。 */
        val tracking: Boolean,
        /** 是否已建立物理角度映射。 */
        val mapped: Boolean,
    ) {
        // data class 含 DoubleArray 时必须手写 equals/hashCode，否则同一性比较会失效
        override fun equals(other: Any?): Boolean {
            if (this === other) return true
            if (other !is Result) return false
            return rawAngleDeg == other.rawAngleDeg &&
                hingeAngleDeg == other.hingeAngleDeg &&
                axis.contentEquals(other.axis) &&
                axisConfidence == other.axisConfidence &&
                axisCalibrated == other.axisCalibrated &&
                tracking == other.tracking &&
                mapped == other.mapped
        }

        override fun hashCode(): Int {
            var result = rawAngleDeg.hashCode()
            result = 31 * result + hingeAngleDeg.hashCode()
            result = 31 * result + axis.contentHashCode()
            result = 31 * result + axisConfidence.hashCode()
            result = 31 * result + axisCalibrated.hashCode()
            result = 31 * result + tracking.hashCode()
            result = 31 * result + mapped.hashCode()
            return result
        }
    }

    /** 默认回退轴：设备 z 轴（屏幕法线）。仅在自动标定之前用于给出方向性参考。 */
    private var axis: DoubleArray = Vec3.of(0.0, 0.0, 1.0)

    private var axisConfidence: Double = 0.0
    private var axisCalibrated: Boolean = false

    /** 校准参考姿态 Q0。 */
    private var reference: Quat? = null

    private var mapping: Mapping? = null

    val currentAxis: DoubleArray get() = axis.copyOf()
    val currentAxisConfidence: Double get() = axisConfidence
    val isAxisCalibrated: Boolean get() = axisCalibrated
    val isTracking: Boolean get() = reference != null
    val currentMapping: Mapping? get() = mapping

    /**
     * 应用一次自动轴标定的结果。
     *
     * 轴可能相对上一次整体反号，此时**必须重算参考姿态**：
     * 角度公式对轴取反是奇函数，若不修正，已建立的角度会瞬间跳到相反数。
     * 解决办法是把参考姿态按新轴重新表达一次（见 [rebaseReference]）。
     */
    fun applyAxisEstimate(result: AxisEstimator.Result) {
        val previousAxis = axis
        val newAxis = Vec3.normalize(result.axis) ?: return

        // 轴反号检测：与旧轴夹角大于 90° 即视为整体翻转
        val flipped = Vec3.dot(newAxis, previousAxis) < 0.0
        axis = newAxis
        axisConfidence = result.confidence
        axisCalibrated = true

        if (flipped) {
            // 轴反号会让所有角度取反，因此必须同步翻转映射的符号
            mapping = mapping?.let {
                Mapping(
                    rawAtClosedDeg = -it.rawAtClosedDeg,
                    rawAtVerticalDeg = -it.rawAtVerticalDeg,
                    minSpanDeg = it.minSpanDeg,
                )
            }
        }
    }

    /**
     * 设定校准参考姿态（点击 Calibrate）。
     *
     * @param reference 当前姿态四元数
     */
    fun setReference(reference: Quat) {
        this.reference = reference.normalizedOrNull()
    }

    /**
     * 记录一个物理角度参考点，用于建立 0~180° 映射。
     *
     * 原始角由调用方传入（即当前 UI 上显示的 raw 值），而不是从内部状态读取——
     * 两个参考点是在**不同时刻**采集的，任何「最近一次解算结果」都会被后续帧覆盖，
     * 显式传参才不会读到过期数据。
     *
     * @param physicalDeg 该参考点对应的物理开合角：0 = 合盖，90 = 屏幕垂直
     * @param rawAngleDeg 该时刻的原始相对角（度）
     */
    fun captureMappingPoint(physicalDeg: Double, rawAngleDeg: Double) {
        val existing = mapping
        mapping = when {
            // 先点「合盖」：另一个点还没给就先按 +90° 占位，等「垂直」点补上后会被覆盖
            physicalDeg <= 0.5 -> Mapping(
                rawAtClosedDeg = rawAngleDeg,
                rawAtVerticalDeg = existing?.rawAtVerticalDeg ?: (rawAngleDeg + 90.0),
            )
            // 再点「垂直」
            physicalDeg >= 89.5 -> Mapping(
                rawAtClosedDeg = existing?.rawAtClosedDeg ?: (rawAngleDeg - 90.0),
                rawAtVerticalDeg = rawAngleDeg,
            )
            else -> existing
        }
    }

    fun clearReference() {
        reference = null
    }

    fun clearMapping() {
        mapping = null
    }

    fun reset() {
        axis = Vec3.of(0.0, 0.0, 1.0)
        axisConfidence = 0.0
        axisCalibrated = false
        reference = null
        mapping = null
    }

    /**
     * 核心解算。
     *
     * @param current 当前姿态（由旋转向量传感器给出）
     */
    fun compute(current: Quat): Result {
        val ref = reference
        if (ref == null) {
            return Result(
                rawAngleDeg = 0.0,
                hingeAngleDeg = 0.0,
                axis = axis.copyOf(),
                axisConfidence = axisConfidence,
                axisCalibrated = axisCalibrated,
                tracking = false,
                mapped = mapping?.isValid == true,
            )
        }

        val qRel = QuatOps.canonical(QuatOps.relative(ref, current) ?: return notTracking())
        val raw = rawAngleDegrees(qRel, axis)

        val map = mapping
        val hinge = if (map != null && map.isValid) map.toPhysicalDegrees(raw) else abs(raw)

        return Result(
            rawAngleDeg = raw,
            hingeAngleDeg = hinge,
            axis = axis.copyOf(),
            axisConfidence = axisConfidence,
            axisCalibrated = axisCalibrated,
            tracking = true,
            mapped = map != null && map.isValid,
        )
    }

    private fun notTracking() = Result(
        rawAngleDeg = 0.0,
        hingeAngleDeg = 0.0,
        axis = axis.copyOf(),
        axisConfidence = axisConfidence,
        axisCalibrated = axisCalibrated,
        tracking = false,
        mapped = mapping?.isValid == true,
    )

    companion object {
        /**
         * 绕 [axis] 扫过的有符号角：2·atan2(q.xyz · axis, q.w)。
         *
         * 推导：q = (cos(θ/2), sin(θ/2)·n)，n 为单位轴。
         * 若 n 与 axis 同向，则 q.xyz · axis = sin(θ/2)，q.w = cos(θ/2)，
         * 于是 atan2 直接还原 θ/2。若 n 与 axis 反向，θ 取负号——这正是「方向」信息。
         */
        fun rawAngleDegrees(qRel: Quat, axis: DoubleArray): Double {
            val unit = Vec3.normalize(axis) ?: return 0.0
            val projected = Vec3.dot(qRel.vector(), unit)
            val angle = 2.0 * atan2(projected, qRel.w)
            return Math.toDegrees(angle)
        }
    }
}

package com.example.laptophinge.sensor

/**
 * 相对姿态运算。
 *
 * 核心公式（规格书第四节）：
 *
 *     Qrelative = inverse(Q0) × Q1
 *
 * 这里有两个必须说清楚的点，否则会得到方向相反的结论：
 *
 * 1. **哪个是 Q0、哪个是 Q1。** Android 的旋转向量表示「设备系 → 世界系」的旋转。
 *    我们要的是「在设备自身坐标系里，屏幕相对校准姿态转了多少」。
 *    把世界系当作中间桥梁：
 *
 *        v_device_now = R1⁻¹ · v_world = R1⁻¹ · R0 · v_device_then
 *        ⇒ 设备系中的相对旋转  R_rel = R1⁻¹ · R0  ⇒  q_rel = q1⁻¹ ⊗ q0
 *
 *    规格书写的 `inverse(Q0) × Q1` 描述的是「世界系中的相对旋转」；
 *    两者互为逆旋转。本项目统一采用**设备系相对旋转**，因为铰链轴是在设备系里
 *    估计出来的（陀螺仪读数就在设备系），两者必须同系才能点乘。
 *
 * 2. **q 的符号是冗余的。** q 与 −q 表示同一个旋转，但向量部分会整体反号。
 *    提取旋转轴时必须先统一到同一半球，否则轴会随机翻转 180°。
 */
object QuatOps {

    /**
     * 设备系中的相对旋转：把 [reference]（校准时的姿态）转到 [current]。
     *
     * @return q_rel，仍满足 |q_rel| = 1
     */
    fun relative(reference: Quat, current: Quat): Quat? =
        (current.conjugate() * reference).normalizedOrNull()

    /** 把四元数统一到 w ≥ 0 的半球，使向量部分方向可复现。 */
    fun canonical(q: Quat): Quat = if (q.w < 0.0) Quat(-q.w, -q.x, -q.y, -q.z) else q

    /**
     * 从「当前姿态」与「上一帧姿态」的差分中提取瞬时旋转轴（设备系）。
     *
     * Δq = q_now⁻¹ ⊗ q_prev 表示设备系中「从 prev 到 now」的旋转，
     * 其向量部分归一化后就是该段旋转的转轴。对单调的旋转向量传感器，
     * 单帧旋转远小于 180°，因此 canonical() 不会破坏轴向。
     *
     * @return 单位轴；旋转过小或数据非法时返回 null
     */
    fun rotationAxisBetween(previous: Quat, current: Quat, minAngleRad: Double = 1e-4): DoubleArray? {
        val dq = canonical(relative(current, previous) ?: return null)
        val axis = Vec3.normalize(dq.vector()) ?: return null
        if (dq.w.coerceIn(-1.0, 1.0).let { 2.0 * kotlin.math.acos(it) } < minAngleRad) return null
        return axis
    }
}

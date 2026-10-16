package com.example.laptophinge.sensor

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import kotlin.math.PI

/**
 * 核心数学模块的纯 JVM 单元测试。
 *
 * 这些模块刻意不依赖任何 Android 类，因此可以在普通 JVM 上直接跑——
 * 传感器行为的调试成本很高（要拿手机、要开合盖子），把数学部分钉死在测试里
 * 才能保证「现场测出来的误差是真实物理误差，不是代码写错了」。
 */
class HingeMathTest {

    private val deg = PI / 180.0

    private fun axisOf(x: Double, y: Double, z: Double) = Vec3.of(x, y, z)

    // ------------------------------------------------------------ 四元数

    @Test
    fun `绕x轴旋转90度把加y轴映射到加z轴`() {
        val q = Quat.fromAxisAngle(axisOf(1.0, 0.0, 0.0), 90.0 * deg)
        val rotated = q.rotate(axisOf(0.0, 1.0, 0.0))
        assertEquals(0.0, rotated[0], 1e-9)
        assertEquals(0.0, rotated[1], 1e-9)
        assertEquals(1.0, rotated[2], 1e-9)
    }

    @Test
    fun `旋转矩阵与向量旋转等价`() {
        // 任意轴，避免只测到退化情形
        val q = Quat.fromAxisAngle(axisOf(0.3, -0.6, 0.74), 47.0 * deg)
        val m = q.toRotationMatrix()
        val v = axisOf(0.2, 0.5, -0.8)

        val byQuat = q.rotate(v)
        val byMatrix = doubleArrayOf(
            m[0] * v[0] + m[1] * v[1] + m[2] * v[2],
            m[3] * v[0] + m[4] * v[1] + m[5] * v[2],
            m[6] * v[0] + m[7] * v[1] + m[8] * v[2],
        )

        for (i in 0..2) assertEquals(byQuat[i], byMatrix[i], 1e-12)
    }

    @Test
    fun `俯仰角符号与Android约定一致`() {
        // 设备 +x 轴指向右侧，抬头（+x 转向世界 +z）应为正俯仰
        val q = Quat.fromAxisAngle(axisOf(1.0, 0.0, 0.0), 30.0 * deg)
        val euler = q.toEulerRadians()
        assertEquals(30.0, Math.toDegrees(euler[1]), 1e-6)
    }

    // ------------------------------------------------------------ 相对旋转

    @Test
    fun `相对旋转能在设备系中还原出绕铰链轴的转动`() {
        val axis = axisOf(0.0, 1.0, 0.0)
        val reference = Quat.fromAxisAngle(axis, -35.0 * deg)
        val current = Quat.fromAxisAngle(axis, 55.0 * deg)

        val qRel = QuatOps.relative(reference, current)
        assertNotNull(qRel)

        // 在设备系里看，参考姿态被整体转了 +90°
        val probe = axisOf(1.0, 0.0, 0.0)
        val rotatedRef = qRel!!.rotate(probe)
        val expected = Quat.fromAxisAngle(axis, 90.0 * deg).rotate(probe)
        for (i in 0..2) assertEquals(expected[i], rotatedRef[i], 1e-9)
    }

    @Test
    fun `铰链轴提取还原绕轴扫过的角`() {
        val axis = axisOf(0.0, 1.0, 0.0)
        val reference = Quat.fromAxisAngle(axis, -20.0 * deg)
        val current = Quat.fromAxisAngle(axis, 25.0 * deg)

        val qRel = QuatOps.relative(reference, current)!!
        val raw = HingeAngleCalculator.rawAngleDegrees(qRel, axis)

        // 从 −20° 转到 +25°，扫过 45°
        assertEquals(45.0, raw, 1e-6)
    }

    @Test
    fun `轴反号时角度反号`() {
        val axis = axisOf(0.0, 1.0, 0.0)
        val qRel = QuatOps.relative(
            Quat.fromAxisAngle(axis, 0.0),
            Quat.fromAxisAngle(axis, 40.0 * deg),
        )!!
        assertEquals(40.0, HingeAngleCalculator.rawAngleDegrees(qRel, axis), 1e-6)
        assertEquals(-40.0, HingeAngleCalculator.rawAngleDegrees(qRel, Vec3.scale(axis, -1.0)), 1e-6)
    }

    @Test
    fun `两帧之间可提取瞬时旋转轴`() {
        val axis = axisOf(1.0, 0.0, 0.0)
        val prev = Quat.fromAxisAngle(axis, 10.0 * deg)
        val now = Quat.fromAxisAngle(axis, 12.0 * deg)

        val extracted = QuatOps.rotationAxisBetween(prev, now)
        assertNotNull(extracted)
        assertEquals(1.0, Math.abs(Vec3.dot(extracted!!, axis)), 1e-6)
    }

    // ------------------------------------------------------------ 铰链轴估计

    @Test
    fun `纯单轴运动时轴估计收敛到真实轴`() {
        val acc = AxisEstimator.Accumulator()
        // 刻意的非轴对齐方向：幂迭代的初始向量是 (1,1,1)，
        // 若真实轴恰好是坐标轴，初始向量的该分量可能为 0 而退化
        val trueAxis = Vec3.normalize(axisOf(0.2, -0.9, 0.39))!!

        // 100 Hz、3 秒，绕真实轴以 1.5 rad/s 转动
        repeat(300) {
            val omega = Vec3.scale(trueAxis, 1.5)
            acc.add(omega, 0.01)
        }

        val est = acc.estimate()
        assertNotNull(est)
        assertEquals(1.0, est!!.confidence, 0.02)
        assertEquals(1.0, Math.abs(Vec3.dot(est.axis, trueAxis)), 1e-6)
        assertEquals(300, est.sampleCount)
    }

    @Test
    fun `双轴乱动会被置信度门槛拒绝`() {
        val acc = AxisEstimator.Accumulator()
        // 一半样本绕 x，一半绕 z —— 主方向不突出
        repeat(200) { acc.add(axisOf(1.0, 0.0, 0.0), 0.01) }
        repeat(200) { acc.add(axisOf(0.0, 0.0, 1.0), 0.01) }

        val est = acc.estimate()
        assertNotNull(est)
        assertTrue(
            "双轴运动置信度应低于门槛 ${AxisEstimator.MIN_AXIS_CONFIDENCE}，实际 ${est!!.confidence}",
            est.confidence < AxisEstimator.MIN_AXIS_CONFIDENCE,
        )
    }

    @Test
    fun `样本不足或转角太小时不出结论`() {
        val acc = AxisEstimator.Accumulator()
        repeat(10) { acc.add(axisOf(0.0, 1.0, 0.0), 0.01) }
        assertNull("样本只有 10 个（门槛 ${AxisEstimator.MIN_SAMPLES}）时不应出结论", acc.estimate())
    }

    @Test
    fun `静止噪声不会污染轴估计`() {
        val acc = AxisEstimator.Accumulator()
        // 低于 MIN_SPEED 的样本应被完全忽略
        repeat(500) { acc.add(axisOf(0.01, 0.01, 0.01), 0.01) }
        assertEquals(0, acc.sampleCount)
        assertNull(acc.estimate())
    }

    // ------------------------------------------------------------ 角度映射

    @Test
    fun `两点标定可正确反解物理角`() {
        val mapping = HingeAngleCalculator.Mapping(
            rawAtClosedDeg = -12.0,
            rawAtVerticalDeg = 78.0,
        )
        assertTrue(mapping.isValid)
        assertEquals(0.0, mapping.toPhysicalDegrees(-12.0), 1e-9)
        assertEquals(90.0, mapping.toPhysicalDegrees(78.0), 1e-9)
        assertEquals(45.0, mapping.toPhysicalDegrees(33.0), 1e-9)
    }

    @Test
    fun `手机装反时映射仍能工作`() {
        // 负斜率：原始角随开合角减小
        val mapping = HingeAngleCalculator.Mapping(
            rawAtClosedDeg = 90.0,
            rawAtVerticalDeg = 0.0,
        )
        assertTrue(mapping.isValid)
        assertEquals(0.0, mapping.toPhysicalDegrees(90.0), 1e-9)
        assertEquals(90.0, mapping.toPhysicalDegrees(0.0), 1e-9)
    }

    @Test
    fun `两个参考点太近时判定为无效标定`() {
        val mapping = HingeAngleCalculator.Mapping(
            rawAtClosedDeg = 10.0,
            rawAtVerticalDeg = 22.0, // 只差 12°，小于 20° 门槛
        )
        assertTrue(!mapping.isValid)
    }

    @Test
    fun `物理角被钳制在0到180度`() {
        val mapping = HingeAngleCalculator.Mapping(
            rawAtClosedDeg = 0.0,
            rawAtVerticalDeg = 90.0,
        )
        assertEquals(0.0, mapping.toPhysicalDegrees(-30.0), 1e-9)
        assertEquals(180.0, mapping.toPhysicalDegrees(200.0), 1e-9)
    }

    @Test
    fun `铰链轴翻转时映射符号同步翻转`() {
        val calc = HingeAngleCalculator()
        val axis = axisOf(0.0, 1.0, 0.0)

        calc.applyAxisEstimate(
            AxisEstimator.Result(axis = axis, confidence = 0.95, sampleCount = 50, totalRotationDeg = 60.0),
        )
        calc.captureMappingPoint(0.0, rawAngleDeg = 0.0)
        calc.captureMappingPoint(90.0, rawAngleDeg = 90.0)

        // 反向轴：角度整体取反，映射必须跟着翻，否则读数会跳到相反数
        calc.applyAxisEstimate(
            AxisEstimator.Result(
                axis = Vec3.scale(axis, -1.0),
                confidence = 0.95,
                sampleCount = 50,
                totalRotationDeg = 60.0,
            ),
        )

        val mapping = calc.currentMapping
        assertNotNull(mapping)
        assertEquals(90.0, mapping!!.rawAtVerticalDeg, 1e-9)
        assertEquals(-0.0, mapping.rawAtClosedDeg, 1e-9)
    }

    // ------------------------------------------------------------ 迟滞状态机

    @Test
    fun `冷启动按当前角度直接定位`() {
        assertEquals(LockStateMachine.State.LOCKED, LockStateMachine().update(10.0))
        assertEquals(LockStateMachine.State.TRACKING, LockStateMachine().update(120.0))
    }

    @Test
    fun `45到50度之间存在迟滞死区`() {
        val sm = LockStateMachine()

        // 从大角度往下降
        sm.update(100.0)
        assertEquals(LockStateMachine.State.TRACKING, sm.update(47.0))
        assertEquals(LockStateMachine.State.LOCKED, sm.update(44.9))

        // 从小角度往上升
        assertEquals(LockStateMachine.State.LOCKED, sm.update(48.0))
        assertEquals(LockStateMachine.State.TRACKING, sm.update(50.0))
    }

    @Test
    fun `阈值边界值行为明确`() {
        val sm = LockStateMachine()
        assertEquals(LockStateMachine.State.LOCKED, sm.update(45.0))
        assertEquals(LockStateMachine.State.LOCKED, sm.update(45.0))
    }

    // ------------------------------------------------------------ 滤波器

    @Test
    fun `滤波器第一帧直接接受不做平滑`() {
        val f = AngleFilter()
        assertEquals(73.5, f.update(73.5, 1_000_000_000L), 1e-9)
    }

    @Test
    fun `静止输入时1欧滤波收敛且抖动被抑制`() {
        val f = AngleFilter(mode = AngleFilter.Mode.ONE_EURO)
        f.update(50.0, 0L)
        var ts = 0L
        repeat(200) {
            ts += 10_000_000L
            // 人为加入 ±2° 噪声，输出应远小于输入噪声
            val noise = if (it % 2 == 0) 2.0 else -2.0
            f.update(50.0 + noise, ts)
        }
        assertEquals(50.0, f.current, 0.6)
    }

    @Test
    fun `快速开合时滤波器跟随而不滞后太多`() {
        val f = AngleFilter(mode = AngleFilter.Mode.ONE_EURO)
        var ts = 0L
        f.update(0.0, ts)
        // 1 秒内从 0° 匀速开到 90°
        repeat(100) {
            ts += 10_000_000L
            f.update(it.toDouble(), ts)
        }
        assertTrue("快速运动时输出应接近真值，实际 ${f.current}", f.current > 85.0)
    }

    @Test
    fun `异常时间戳不会让滤波器发散`() {
        val f = AngleFilter()
        f.update(10.0, 1_000_000_000L)
        // 时间戳倒退
        val out = f.update(11.0, 500_000_000L)
        assertTrue(out.isFinite())
    }

    // ------------------------------------------------------------ 工具函数

    @Test
    fun `零向量归一化返回null`() {
        assertNull(Vec3.normalize(axisOf(0.0, 0.0, 0.0)))
    }

    @Test
    fun `alignTo把向量统一到同一半球`() {
        val ref = axisOf(0.0, 1.0, 0.0)
        val flipped = Vec3.alignTo(axisOf(0.0, -1.0, 0.0), ref)
        assertEquals(1.0, Vec3.dot(flipped, ref), 1e-12)
    }
}

using SimosaBRM.SimCore.Environment;

namespace SimosaBRM.SimCore.Physics;

/// <summary>
/// 自船運動方程式的介面:輸入狀態向量、環境(風、流、水深)、控制(舵角、轉速、側推),回傳狀態導數,
/// 由固定步長 RK4(<see cref="Rk4Integrator"/>)積分。
/// MMG 完整模型(規劃書第 6.2 節)移植時實作此介面:在 <see cref="Derivative"/> 內計算 X_H+X_P+X_R+X_W…,
/// 以 (m+m_x) 等除得 u̇、v̇、ṙ;運動學部分(ẋ、ẏ、ψ̇,含均勻流)可直接呼叫 <see cref="Kinematics"/>。
/// 模型必須是純函數(不得保留跨步的內部狀態),否則快照/還原與重播的確定性會失效;
/// 需要狀態的致動器(舵機、主機)已在 <see cref="ActuatorModel"/> 另行處理。
/// </summary>
public interface IShipDynamics
{
    /// <summary>模型名稱與版本(寫入紀錄檔標頭,重播時核對)。</summary>
    string ModelName { get; }

    /// <summary>計算導數。<paramref name="t"/> 為模擬時間(秒),供時變模型使用。</summary>
    StateVector Derivative(double t, in StateVector state, in EnvironmentSample env, in ControlInput control);

    /// <summary>船體座標對水速度 + 均勻流 → ENU 位置導數與航向導數(所有模型共用)。</summary>
    static (double DX, double DY, double DPsi) Kinematics(in StateVector s, in EnvironmentSample env)
    {
        var c = Math.Cos(s.Psi);
        var sn = Math.Sin(s.Psi);
        return (s.U * sn + s.V * c + env.CurrentEastMps,
                s.U * c - s.V * sn + env.CurrentNorthMps,
                s.R);
    }
}

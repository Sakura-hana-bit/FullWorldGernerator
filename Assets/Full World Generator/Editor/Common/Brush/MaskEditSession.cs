using FullWorld;
using System;
using UnityEditor;
using UnityEngine;

namespace FullWorldEditor
{
    /// <summary>
    /// 全局唯一的 Mask 编辑焦点管理器。
    /// 维护当前编辑目标，并通知订阅者焦点变更。
    /// 触发方式：双击任意 Mask 缩略图 → Activate(maskData)
    /// 
    /// 自动保存时机：
    ///   - Activate 新 mask 时，自动保存旧 mask
    ///   - Deactivate 时保存
    ///   - 脚本重编译 / Domain Reload 前自动保存
    /// 
    /// Undo 机制（6 级，逐级降分辨率）：
    ///   - Slot 0: 等分辨率 → 撤销最近一次笔划
    ///   - Slot 1: 1/2 分辨率 → 撤销上上次笔划
    ///   - Slot 2: 1/4 分辨率 → 撤销上上上次笔划
    ///   - Slot 3: 1/8 分辨率
    ///   - Slot 4: 1/16 分辨率
    ///   - Slot 5: 1/32 分辨率（最旧，第 6 次之前的丢弃）
    ///   
    ///   每次笔划开始前 CaptureUndoSnapshot()：
    ///     1. 旧 Slot[i] → 降采样到 Slot[i+1] 分辨率并覆盖
    ///     2. 当前 mask 状态 CopyTexture → Slot[0]
    ///   
    ///   PerformUndo()：
    ///     取最深的有效 Slot，上采样回写到 mask RT
    /// </summary>
    public class MaskEditSession
    {
        #region Singleton

        private static MaskEditSession s_Instance;
        public static MaskEditSession Instance => s_Instance ??= new MaskEditSession();

        public static void Destroy()
        {
            s_Instance?.ForceSaveCurrent();
            s_Instance?.ReleaseUndoSlots();
            s_Instance = null;
        }

        #endregion

        #region Undo Slots (fixed 6 levels, decreasing resolution)

        private const int k_UndoSlotCount = 6;

        /// <summary>
        /// Slot[i] 分辨率 = baseResolution / 2^i。
        /// Slot[0] = 等分辨率（最近笔划），Slot[5] = 1/32 分辨率（最旧）。
        /// </summary>
        private readonly RenderTexture[] m_UndoSlots = new RenderTexture[k_UndoSlotCount];
        private readonly bool[] m_UndoSlotValid = new bool[k_UndoSlotCount];
        private int m_UndoBaseResolution;

        #endregion

        #region State

        /// <summary>当前激活的 Mask，null 表示无编辑目标。</summary>
        public BaseMaskData Current { get; private set; }

        #endregion

        #region Events

        /// <summary>焦点变更时触发，参数为新 Mask（可能为 null）。</summary>
        public event Action<BaseMaskData> OnChanged;

        /// <summary>每次笔刷 stamp 后触发，用于驱动地形实时重生成。</summary>
        public event Action OnStrokePainted;

        /// <summary>Undo 执行后触发，通知订阅者刷新预览/重生成。</summary>
        public event Action OnUndoPerformed;

        #endregion

        #region Constructor

        private MaskEditSession()
        {
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
        }

        #endregion

        #region Public API

        /// <summary>笔刷每次 stamp 后调用，通知订阅者 mask 已更新。</summary>
        public void NotifyStrokePainted() => OnStrokePainted?.Invoke();

        /// <summary>激活指定 Mask 进入编辑状态。若已有编辑中的 Mask，自动保存后再切换。</summary>
        public void Activate(BaseMaskData mask)
        {
            if (Current == mask) return;

            // 保存旧的编辑目标
            if (Current != null)
            {
                ForceSaveCurrent();
                OnChanged?.Invoke(null);
            }

            Current = mask;
            InvalidateUndoSlots();

            OnChanged?.Invoke(Current);
        }

        /// <summary>退出编辑状态，保存并清除焦点。</summary>
        public void Deactivate()
        {
            if (Current == null) return;

            OnChanged?.Invoke(null);

            ForceSaveCurrent();
            Current = null;
            InvalidateUndoSlots();
        }

        /// <summary>强制将当前 mask 的 GPU 数据回写到 CPU 并标记 Dirty。</summary>
        public void ForceSaveCurrent()
        {
            if (Current == null) return;
            Current.SyncToCpu();
            EditorUtility.SetDirty(Current);
        }

        #endregion

        #region Undo API

        /// <summary>当前可 Undo 的步数。</summary>
        public int UndoCount
        {
            get
            {
                int count = 0;
                for (int i = 0; i < k_UndoSlotCount; i++)
                    if (m_UndoSlotValid[i]) count++;
                return count;
            }
        }

        /// <summary>
        /// 在笔划开始前调用。
        /// 将所有旧快照下移一级（Slot[i] → 降采样到 Slot[i+1] 分辨率），
        /// 然后把当前 mask RT 的内容 CopyTexture 到 Slot[0]（等分辨率）。
        /// 最旧的 Slot[5] 被覆盖丢弃。
        /// </summary>
        public void CaptureUndoSnapshot()
        {
            if (Current == null) return;
            var rt = Current.EnsureEditableRT();
            if (rt == null) return;

            int resolution = rt.width;
            EnsureUndoSlots(resolution);

            // 从底部往上移：Slot[4]→Slot[5], Slot[3]→Slot[4], ..., Slot[0]→Slot[1]
            // 每个 Slot[i-1] 的内容降采样到 Slot[i] 的分辨率
            for (int i = k_UndoSlotCount - 1; i > 0; i--)
            {
                if (m_UndoSlotValid[i - 1])
                {
                    var prev = RenderTexture.active;
                    RenderTexture.active = m_UndoSlots[i];
                    Graphics.Blit(m_UndoSlots[i - 1], m_UndoSlots[i]);
                    RenderTexture.active = prev;
                    m_UndoSlotValid[i] = true;
                }
                else
                {
                    m_UndoSlotValid[i] = false;
                }
            }

            // 当前 mask 状态 → Slot[0]（等分辨率，CopyTexture 零损耗）
            Graphics.CopyTexture(rt, m_UndoSlots[0]);
            m_UndoSlotValid[0] = true;
        }

        /// <summary>
        /// 撤销最近一次笔划。
        /// 找到最浅的有效 Slot（分辨率最高 = 最近的笔划），
        /// 将其内容回写到 mask RT，然后标记该 Slot 为已消耗。
        /// 若 Slot 分辨率低于 mask RT，通过 Blit 上采样。
        /// </summary>
        /// <returns>true 如果成功执行 Undo。</returns>
        public bool PerformUndo()
        {
            if (Current == null) return false;
            var rt = Current.EnsureEditableRT();
            if (rt == null) return false;

            // 从最浅（最高分辨率/最近）开始找
            for (int i = 0; i < k_UndoSlotCount; i++)
            {
                if (!m_UndoSlotValid[i]) continue;

                var slot = m_UndoSlots[i];

                if (slot.width == rt.width)
                {
                    // 等分辨率直接回写
                    Graphics.CopyTexture(slot, rt);
                }
                else
                {
                    // 低分辨率上采样回写
                    var prev = RenderTexture.active;
                    RenderTexture.active = rt;
                    Graphics.Blit(slot, rt);
                    RenderTexture.active = prev;
                }

                m_UndoSlotValid[i] = false;

                // 仅恢复 GPU 端 RT，不碰 CPU。
                // MarkDirty 会导致 PreviewRT 从旧 m_Mask 重新生成，覆盖刚恢复的 GPU 数据，
                // 所以这里要显式保持 RT 可用、dirty=false。
                Current.ClearDirty();

                OnUndoPerformed?.Invoke();
                return true;
            }

            return false;
        }

        #endregion

        #region Undo Slot Lifecycle

        /// <summary>
        /// 确保 6 个 Slot RT 已就绪，分辨率与当前 mask 一致。
        /// Slot[i] 分辨率 = max(1, baseResolution >> i)。
        /// 若分辨率变化则重建所有 Slot 并清空 Undo 历史。
        /// </summary>
        private void EnsureUndoSlots(int baseResolution)
        {
            if (m_UndoBaseResolution == baseResolution) return;
            m_UndoBaseResolution = baseResolution;

            for (int i = 0; i < k_UndoSlotCount; i++)
            {
                int slotRes = Mathf.Max(1, baseResolution >> i);

                if (m_UndoSlots[i] != null)
                {
                    if (m_UndoSlots[i].width == slotRes) continue;
                    m_UndoSlots[i].Release();
                    UnityEngine.Object.DestroyImmediate(m_UndoSlots[i]);
                }

                m_UndoSlots[i] = CreateUndoRT(slotRes);
                m_UndoSlotValid[i] = false;
            }
        }

        private static RenderTexture CreateUndoRT(int size)
        {
            var rt = new RenderTexture(size, size, 0, RenderTextureFormat.ARGBFloat)
            {
                enableRandomWrite = true,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            rt.Create();
            return rt;
        }

        /// <summary>标记所有 Slot 为无效（不清除 RT，下次 Capture 可复用）。</summary>
        private void InvalidateUndoSlots()
        {
            for (int i = 0; i < k_UndoSlotCount; i++)
                m_UndoSlotValid[i] = false;
        }

        /// <summary>释放所有 Slot RT 资源。</summary>
        private void ReleaseUndoSlots()
        {
            for (int i = 0; i < k_UndoSlotCount; i++)
            {
                if (m_UndoSlots[i] != null)
                {
                    m_UndoSlots[i].Release();
                    UnityEngine.Object.DestroyImmediate(m_UndoSlots[i]);
                    m_UndoSlots[i] = null;
                }
                m_UndoSlotValid[i] = false;
            }
            m_UndoBaseResolution = 0;
        }

        #endregion

        #region Domain Reload

        private void OnBeforeAssemblyReload()
        {
            ForceSaveCurrent();
        }

        #endregion
    }
}

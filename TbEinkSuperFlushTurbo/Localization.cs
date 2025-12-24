using System;
using System.Collections.Generic;
using System.Globalization;

namespace TbEinkSuperFlushTurbo
{
    // Localization resource class
    public static class Localization
    {
        public enum Language
        {
            English,
            ChineseSimplified,
            ChineseTraditional
        }

        private static Language _currentLanguage = Language.English;
        private static readonly Dictionary<string, Dictionary<Language, string>> _resources = new()
        {
            // Window title
            ["WindowTitle"] = new()
            {
                [Language.English] = "Eink Kaleido Ghost Reducer (GPU)",
                [Language.ChineseSimplified] = "Eink Kaleido 残影清除器 (GPU)",
                [Language.ChineseTraditional] = "Eink Kaleido 殘影清除器 (GPU)"
            },
            
            // Button text
            ["Start"] = new()
            {
                [Language.English] = "Start",
                [Language.ChineseSimplified] = "开始",
                [Language.ChineseTraditional] = "開始"
            },
            
            ["Stop"] = new()
            {
                [Language.English] = "Stop",
                [Language.ChineseSimplified] = "停止",
                [Language.ChineseTraditional] = "停止"
            },
            
            // Label text
            ["PixelColorDiff"] = new()
            {
                [Language.English] = "Pixel Color Diff:",
                [Language.ChineseSimplified] = "像素颜色差异：",
                [Language.ChineseTraditional] = "像素顏色差異："
            },
            
            ["DetectInterval"] = new()
            {
                [Language.English] = "Tile Pixel Length:",
                [Language.ChineseSimplified] = "区块像素边长：",
                [Language.ChineseTraditional] = "區塊像素邊長："
            },
            
            ["ToggleHotkey"] = new()
            {
                [Language.English] = "Toggle Switch Hotkey:",
                [Language.ChineseSimplified] = "切换开关快捷键：",
                [Language.ChineseTraditional] = "切換開關快捷鍵："
            },
            
            // Units
            ["Milliseconds"] = new()
            {
                [Language.English] = "ms",
                [Language.ChineseSimplified] = "毫秒",
                [Language.ChineseTraditional] = "毫秒"
            },
            
            ["Pixels"] = new()
            {
                [Language.English] = "px",
                [Language.ChineseSimplified] = "像素",
                [Language.ChineseTraditional] = "像素"
            },
            
            // Status text
            ["StatusStopped"] = new()
            {
                [Language.English] = "Status: Stopped",
                [Language.ChineseSimplified] = "状态：已停止",
                [Language.ChineseTraditional] = "狀態：已停止"
            },
            
            ["StatusRunning"] = new()
            {
                [Language.English] = "Status: Running - Display: {0}{1}, Resolution: {2}{3}",
                [Language.ChineseSimplified] = "状态：运行中 - 显示器: {0}{1}, 分辨率: {2}{3}",
                [Language.ChineseTraditional] = "狀態：運行中 - 顯示器: {0}{1}, 解析度: {2}{3}"
            },
            
            ["StatusStoppedWithDisplay"] = new()
            {
                [Language.English] = "Status: Stopped - Display: {0}{1}, Resolution: {2}{3}",
                [Language.ChineseSimplified] = "状态：已停止 - 显示器: {0}{1}, 分辨率: {2}{3}",
                [Language.ChineseTraditional] = "狀態：已停止 - 顯示器: {0}{1}, 解析度: {2}{3}"
            },
            
            ["StatusInitializing"] = new()
            {
                [Language.English] = "Status: Initializing GPU capture...",
                [Language.ChineseSimplified] = "状态：正在初始化GPU捕获...",
                [Language.ChineseTraditional] = "狀態：正在初始化GPU捕獲..."
            },
            
            ["StatusFailed"] = new()
            {
                [Language.English] = "Status: Failed",
                [Language.ChineseSimplified] = "状态：失败",
                [Language.ChineseTraditional] = "狀態：失敗"
            },
            
            ["StatusReady"] = new()
            {
                [Language.English] = "Status: Ready",
                [Language.ChineseSimplified] = "状态：就绪",
                [Language.ChineseTraditional] = "狀態：就緒"
            },
            
            // Hotkey related
            ["ClickButtonToSet"] = new()
            {
                [Language.English] = "click button to set hotkey",
                [Language.ChineseSimplified] = "点击按钮设置快捷键",
                [Language.ChineseTraditional] = "點擊按鈕設置快捷鍵"
            },
            
            ["PressHotkeyCombination"] = new()
            {
                [Language.English] = "Press hotkey combination...",
                [Language.ChineseSimplified] = "按下快捷键组合...",
                [Language.ChineseTraditional] = "按下快捷鍵組合..."
            },
            
            // Question mark button
            ["QuestionMark"] = new()
            {
                [Language.English] = "?",
                [Language.ChineseSimplified] = "？",
                [Language.ChineseTraditional] = "？"
            },

            // Settings form
            ["SettingsTitle"] = new()
            {
                [Language.English] = "Settings - 设置",
                [Language.ChineseSimplified] = "设置",
                [Language.ChineseTraditional] = "設置"
            },

            ["StopOver59Hz"] = new()
            {
                [Language.English] = "Disallow operation on displays over 59Hz",
                [Language.ChineseSimplified] = "禁止在超过59Hz的显示器上运行",
                [Language.ChineseTraditional] = "禁止在超過59Hz的顯示器上運行"
            },

            ["OK"] = new()
            {
                [Language.English] = "OK",
                [Language.ChineseSimplified] = "确定",
                [Language.ChineseTraditional] = "確定"
            },

            ["Cancel"] = new()
            {
                [Language.English] = "Cancel",
                [Language.ChineseSimplified] = "取消",
                [Language.ChineseTraditional] = "取消"
            },
            
            // Display selection
            ["DisplaySelection"] = new()
            {
                [Language.English] = "Display Selection:",
                [Language.ChineseSimplified] = "显示器选择：",
                [Language.ChineseTraditional] = "顯示器選擇："
            },
            
            // Primary display marker
            ["Primary"] = new()
            {
                [Language.English] = "Primary",
                [Language.ChineseSimplified] = "主",
                [Language.ChineseTraditional] = "主"
            },
            
            // Resolution related
            ["Physical"] = new()
            {
                [Language.English] = "Physical",
                [Language.ChineseSimplified] = "物理",
                [Language.ChineseTraditional] = "物理"
            },
            
            ["Logical"] = new()
            {
                [Language.English] = "Logical",
                [Language.ChineseSimplified] = "逻辑",
                [Language.ChineseTraditional] = "邏輯"
            },
            
            ["Resolution"] = new()
            {
                [Language.English] = "Resolution",
                [Language.ChineseSimplified] = "分辨率",
                [Language.ChineseTraditional] = "解析度"
            },
            
            ["Scale"] = new()
            {
                [Language.English] = "Scale",
                [Language.ChineseSimplified] = "缩放",
                [Language.ChineseTraditional] = "縮放"
            },
            
            ["TileSize"] = new()
            {
                [Language.English] = "Tile Size",
                [Language.ChineseSimplified] = "区块尺寸",
                [Language.ChineseTraditional] = "區塊尺寸"
            },
            
            // Tray icon
            ["TrayIconText"] = new()
            {
                [Language.English] = "Eink Ghost Reducer",
                [Language.ChineseSimplified] = "Eink 残影清除器",
                [Language.ChineseTraditional] = "Eink 殘影清除器"
            },
            
            // Tray menu items
            ["ShowPanel"] = new()
            {
                [Language.English] = "Show Panel",
                [Language.ChineseSimplified] = "显示面板",
                [Language.ChineseTraditional] = "顯示面板"
            },
            
            ["Exit"] = new()
            {
                [Language.English] = "Exit",
                [Language.ChineseSimplified] = "退出",
                [Language.ChineseTraditional] = "退出"
            },
            
            // Tray notification messages
            ["MinimizedToTrayTitle"] = new()
            {
                [Language.English] = "Minimized to Tray",
                [Language.ChineseSimplified] = "已最小化到托盘",
                [Language.ChineseTraditional] = "已最小化到托盤"
            },
            
            ["MinimizedToTrayMessage"] = new()
            {
                [Language.English] = "The application has been minimized to the system tray. You can restore it by clicking the tray icon.",
                [Language.ChineseSimplified] = "程序已最小化到系统托盘。您可以通过点击托盘图标来恢复它。",
                [Language.ChineseTraditional] = "程式已最小化到系統托盤。您可以通過點擊托盤圖標來恢復它。"
            },
            
            // Capture status notification messages
            ["CaptureStartedTitle"] = new()
            {
                [Language.English] = "Capture Started",
                [Language.ChineseSimplified] = "捕获已开始",
                [Language.ChineseTraditional] = "捕獲已開始"
            },
            
            ["CaptureStartedMessage"] = new()
            {
                [Language.English] = "Screen capture has started.",
                [Language.ChineseSimplified] = "屏幕捕获已开始。",
                [Language.ChineseTraditional] = "螢幕捕獲已開始。"
            },
            
            ["CaptureStoppedTitle"] = new()
            {
                [Language.English] = "Capture Stopped",
                [Language.ChineseSimplified] = "捕获已停止",
                [Language.ChineseTraditional] = "捕獲已停止"
            },
            
            ["CaptureStoppedMessage"] = new()
            {
                [Language.English] = "Screen capture has stopped.",
                [Language.ChineseSimplified] = "屏幕捕获已停止。",
                [Language.ChineseTraditional] = "螢幕捕獲已停止。"
            },
            
            // Error notification messages
            ["CannotStartWhileRecordingHotkey"] = new()
            {
                [Language.English] = "Cannot start screen capture while recording hotkey, Please complete hotkey recording first.",
                [Language.ChineseSimplified] = "无法在录制热键时启动截屏，请先完成热键录制。",
                [Language.ChineseTraditional] = "無法在錄製熱鍵時啟動截圖，請先完成熱鍵錄製。"
            },
            
            ["HighRefreshRateWarning"] = new()
            {
                [Language.English] = "To avoid mis-selection, screen capture is disabled by default on displays over 59Hz. Current display refresh rate is {0:F1}Hz. If your e-ink display is over 59Hz or refresh rate detection is incorrect, please click the gear button to disable this restriction.",
                [Language.ChineseSimplified] = "为了避免误选择，默认禁止在超过59Hz的显示器上运行。当前显示器刷新率为{0:F1}Hz。若您的墨水屏超过59Hz或刷新率检测错误，请点击齿轮关闭此限制",
                [Language.ChineseTraditional] = "為了避免誤選擇，預設禁止在超過59Hz的顯示器上執行。當前顯示器刷新率為{0:F1}Hz。若您的墨水屏超過59Hz或刷新率偵測錯誤，請點擊齒輪關閉此限制"
            },
            
            ["DisplayChangeAutoStop"] = new()
            {
                [Language.English] = "Detected {0}. Screen refresh has been automatically stopped. Please reselect the display and start.",
                [Language.ChineseSimplified] = "检测到{0}，刷新已自动停止。请重新选择显示器后开始。",
                [Language.ChineseTraditional] = "偵測到{0}，刷新已自動停止。請重新選擇顯示器後開始。"
            },
            
            ["CannotModifySettingsWhileRunning"] = new()
            {
                [Language.English] = "Screen capture is running, please stop capture first before modifying settings.",
                [Language.ChineseSimplified] = "截屏运行中，请先停止截屏再修改设置。",
                [Language.ChineseTraditional] = "截圖執行中，請先停止截圖再修改設定。"
            },

            ["HighRefreshRateCurrentWarning"] = new()
            {
                [Language.English] = "Current display refresh rate is {0:F1}Hz, exceeding 59Hz limit. The program will automatically stop when you click Start.",
                [Language.ChineseSimplified] = "当前显示器刷新率为 {0:F1}Hz，超过59Hz限制。程序将在您点击开始按钮时自动停止。",
                [Language.ChineseTraditional] = "當前顯示器刷新率為 {0:F1}Hz，超過59Hz限制。程式將在您點擊開始按鈕時自動停止。"
            },

            ["CannotSwitchDisplayWhileCapturing"] = new()
            {
                [Language.English] = "Screen capture is running. Please stop capture first before switching display.",
                [Language.ChineseSimplified] = "截屏运行中，停止截屏后才能切换显示器。",
                [Language.ChineseTraditional] = "截圖執行中，停止截圖後才能切換顯示器。"
            },

            ["PixelDiffThresholdHelpTitle"] = new()
            {
                [Language.English] = "Pixel Color Diff Threshold",
                [Language.ChineseSimplified] = "像素颜色差异阈值说明",
                [Language.ChineseTraditional] = "像素顏色差異閾值說明"
            },

            ["PixelDiffThresholdHelpContent"] = new()
            {
                [Language.English] = "Pixel Color Diff Threshold:\n\nControls the sensitivity to luminance changes in individual color channels (R/G/B) for each tile.\n\nLower values (2-8): Better for default light themes, detects subtle changes (Distinguishes white from light gray and low-brightness colors)\n\nHigher values (15-25): Better for high-contrast themes, ignores minor variations\n\nRecommended: Start with 10 and adjust based on your theme.",
                [Language.ChineseSimplified] = "像素颜色差异阈值说明:\n\n控制区块内每个颜色通道(R/G/B)亮度变化的敏感度。\n\n较低值(2-8): 适合默认浅色主题，检测细微变化（区分白色和浅灰色及浅亮度彩色）\n\n较高值(15-25): 适合高对比度主题，忽略微小变化\n\n推荐: 从10开始，根据您的主题进行调整。",
                [Language.ChineseTraditional] = "像素顏色差異閾值說明:\n\n控制區塊內每個顏色通道(R/G/B)亮度變化的敏感度。\n\n較低值(2-8): 適合預設淺色主題，檢測細微變化（區分白色和淺灰色及淺亮度彩色）\n\n較高值(15-25): 適合高對比度主題，忽略微小變化\n\n推薦: 從10開始，根據您的主題進行調整。"
            },

            ["CannotModifyHotkeyWhileRunning"] = new()
            {
                [Language.English] = "Cannot modify hotkey while screen capture is running. Please stop screen capture first.",
                [Language.ChineseSimplified] = "运行时无法修改热键，请先停止截屏。",
                [Language.ChineseTraditional] = "運行時無法修改熱鍵，請先停止截圖。"
            },

            ["ResolutionSeparator"] = new()
            {
                [Language.English] = "Resolution:",
                [Language.ChineseSimplified] = "分辨率:",
                [Language.ChineseTraditional] = "解析度:"
            },
            
            ["DisplaySettingsChange"] = new()
            {
                [Language.English] = "Display settings change",
                [Language.ChineseSimplified] = "显示器设置更改",
                [Language.ChineseTraditional] = "顯示器設定更改"
            },
            
            ["DisplayConfigurationChange"] = new()
            {
                [Language.English] = "Display configuration change",
                [Language.ChineseSimplified] = "显示器配置更改",
                [Language.ChineseTraditional] = "顯示器配置更改"
            },
            
            ["DisplayCountChange"] = new()
            {
                [Language.English] = "Display count change",
                [Language.ChineseSimplified] = "显示器数量变化",
                [Language.ChineseTraditional] = "顯示器數量變化"
            }
        };

        // Detect system language and set current language
        public static void DetectAndSetLanguage()
        {
            CultureInfo currentCulture = CultureInfo.CurrentCulture;
            string cultureName = currentCulture.Name.ToLower();
            
            if (cultureName.StartsWith("zh"))
            {
                if (cultureName.Contains("hant") || cultureName.Contains("tw") || cultureName.Contains("hk") || cultureName.Contains("mo"))
                {
                    _currentLanguage = Language.ChineseTraditional;
                }
                else
                {
                    _currentLanguage = Language.ChineseSimplified;
                }
            }
            else
            {
                _currentLanguage = Language.English;
            }
        }

        // Get localized text
        public static string GetText(string key)
        {
            if (_resources.ContainsKey(key) && _resources[key].ContainsKey(_currentLanguage))
            {
                return _resources[key][_currentLanguage];
            }
            
            // If the corresponding language is not found, return English
            if (_resources.ContainsKey(key) && _resources[key].ContainsKey(Language.English))
            {
                return _resources[key][Language.English];
            }
            
            return key; // If all not found, return the key itself
        }

        // Get current language
        public static Language CurrentLanguage => _currentLanguage;
    }
}
using System;
using System.Collections.Generic;
using System.Globalization;

namespace TbEinkSuperFlushTurbo
{
    // 本地化资源类
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
            // 窗口标题
            ["WindowTitle"] = new()
            {
                [Language.English] = "EInk Kaleido Ghost Reducer (GPU)",
                [Language.ChineseSimplified] = "EInk Kaleido 残影清除器 (GPU)",
                [Language.ChineseTraditional] = "EInk Kaleido 殘影清除器 (GPU)"
            },
            
            // 按钮文本
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
            
            // 标签文本
            ["PixelColorDiff"] = new()
            {
                [Language.English] = "Pixel Color Diff:",
                [Language.ChineseSimplified] = "像素颜色差异：",
                [Language.ChineseTraditional] = "像素顏色差異："
            },
            
            ["DetectInterval"] = new()
            {
                [Language.English] = "Detect Interval:",
                [Language.ChineseSimplified] = "检测间隔：",
                [Language.ChineseTraditional] = "檢測間隔："
            },
            
            ["ToggleHotkey"] = new()
            {
                [Language.English] = "Toggle Status Hotkey:",
                [Language.ChineseSimplified] = "切换运行状态快捷键：",
                [Language.ChineseTraditional] = "切換運行狀態快捷鍵："
            },
            
            // 单位
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
            
            // 状态文本
            ["StatusStopped"] = new()
            {
                [Language.English] = "Status: Stopped",
                [Language.ChineseSimplified] = "状态：已停止",
                [Language.ChineseTraditional] = "狀態：已停止"
            },
            
            ["StatusRunning"] = new()
            {
                [Language.English] = "Status: Running",
                [Language.ChineseSimplified] = "状态：运行中",
                [Language.ChineseTraditional] = "狀態：運行中"
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
            
            // 热键相关
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
            
            // 问号按钮
            ["QuestionMark"] = new()
            {
                [Language.English] = "?",
                [Language.ChineseSimplified] = "？",
                [Language.ChineseTraditional] = "？"
            },
            
            // 托盘图标
            ["TrayIconText"] = new()
            {
                [Language.English] = "EInk Ghost Reducer",
                [Language.ChineseSimplified] = "EInk 残影清除器",
                [Language.ChineseTraditional] = "EInk 殘影清除器"
            },
            
            // 托盘菜单项
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
            
            // 托盘提示信息
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
            
            // 捕获状态提示信息
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
            }
        };

        // 检测系统语言并设置当前语言
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

        // 获取本地化文本
        public static string GetText(string key)
        {
            if (_resources.ContainsKey(key) && _resources[key].ContainsKey(_currentLanguage))
            {
                return _resources[key][_currentLanguage];
            }
            
            // 如果找不到对应的语言，返回英文
            if (_resources.ContainsKey(key) && _resources[key].ContainsKey(Language.English))
            {
                return _resources[key][Language.English];
            }
            
            return key; // 如果都找不到，返回key本身
        }

        // 获取当前语言
        public static Language CurrentLanguage => _currentLanguage;
    }
}
cbuffer Params : register(b0)
{
    uint screenW;
    uint screenH;
    uint tileSize;
    uint pixelDeltaThreshold;
    uint averageWindowSize;
    uint stableFramesRequired;
    uint additionalCooldownFrames;
    uint firstRefreshExtraDelay;
    uint currentFrameNumber;
    uint protectionFrames; // 新增：从C#传入的保护期帧数
}

Texture2D<float4> g_texPrev : register(t0);
Texture2D<float4> g_texCurr : register(t1);

// 输入/输出历史差异数据
RWStructuredBuffer<uint4> g_tileHistoryIn : register(u0);
RWStructuredBuffer<uint4> g_tileHistoryOut : register(u1);

// 输出的刷新列表和计数器
RWStructuredBuffer<uint> g_refreshList : register(u2);
RWByteAddressBuffer g_refreshCounter : register(u3);

// 输出的亮度数据
RWStructuredBuffer<float> g_tileBrightness : register(u4);

// 输入/输出的图块状态 (由C#管理)
RWStructuredBuffer<int> g_tileStableCounters : register(u5);
RWStructuredBuffer<uint2> g_tileProtectionExpiry : register(u6); // 使用 uint2 表示64位值 (low, high)


[numthreads(8, 8, 1)]
void CSMain(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint tilesX = (screenW + tileSize - 1) / tileSize;
    uint tilesY = (screenH + tileSize - 1) / tileSize;
    
    if (dispatchThreadId.x >= tilesX || dispatchThreadId.y >= tilesY)
        return;
        
    uint tileIdx = dispatchThreadId.y * tilesX + dispatchThreadId.x;
    uint tileX = dispatchThreadId.x * tileSize;
    uint tileY = dispatchThreadId.y * tileSize;

    // 1. 计算当前帧的像素差异总和以及平均亮度
    uint currentFrameTileDiffSum = 0;
    float tileBrightnessSum = 0.0f;
    uint pixelCount = 0;
    
    for (uint y = 0; y < tileSize; ++y)
    {
        for (uint x = 0; x < tileSize; ++x)
        {
            uint pixelX = tileX + x;
            uint pixelY = tileY + y;

            if (pixelX < screenW && pixelY < screenH)
            {
                float4 prevColor = g_texPrev[uint2(pixelX, pixelY)];
                float4 currColor = g_texCurr[uint2(pixelX, pixelY)];

                // 计算RGB三分量的绝对差值之和
                currentFrameTileDiffSum += (uint)(abs(prevColor.r - currColor.r) * 255.0f);
                currentFrameTileDiffSum += (uint)(abs(prevColor.g - currColor.g) * 255.0f);
                currentFrameTileDiffSum += (uint)(abs(prevColor.b - currColor.b) * 255.0f);
                
                // 计算亮度 (用于OverlayForm)
                float luminance = 0.299f * currColor.r + 0.587f * currColor.g + 0.114f * currColor.b;
                tileBrightnessSum += luminance;
                pixelCount++;
            }
        }
    }
    
    // 写入平均亮度
    if (pixelCount > 0)
    {
        g_tileBrightness[tileIdx] = tileBrightnessSum / (float)pixelCount;
    }
    else
    {
        g_tileBrightness[tileIdx] = 0.0f;
    }

    // 2. 更新历史差异记录 (用于未来的平均值计算)
    uint4 historyIn = g_tileHistoryIn[tileIdx];
    uint4 historyOut;
    historyOut.xyz = historyIn.yzw; // Shift history
    historyOut.w = currentFrameTileDiffSum;
    g_tileHistoryOut[tileIdx] = historyOut;

    // 3. 计算滑动窗口内的平均差异
    uint sumForAverage = 0;
    if (averageWindowSize >= 1) sumForAverage += currentFrameTileDiffSum;
    if (averageWindowSize >= 2) sumForAverage += historyIn.w;
    if (averageWindowSize >= 3) sumForAverage += historyIn.z;
    if (averageWindowSize >= 4) sumForAverage += historyIn.y;
    if (averageWindowSize >= 5) sumForAverage += historyIn.x;

    uint effectiveAverageWindowSize = max(1, averageWindowSize);
    uint averageDiff = sumForAverage / effectiveAverageWindowSize;

    // 4. 核心逻辑：根据差异和状态决定是否刷新
    int stableCounter = g_tileStableCounters[tileIdx];
    uint2 expiryFrame = g_tileProtectionExpiry[tileIdx];
    
    // 比较 uint2 表示的64位值
    // 先比较高位，如果高位相等再比较低位
    bool inProtection = false;
    if (expiryFrame.y > 0)
    {
        inProtection = true; // 高位大于0，肯定大于currentFrameNumber（currentFrameNumber的高位为0）
    }
    else if (expiryFrame.y == 0)
    {
        inProtection = expiryFrame.x > currentFrameNumber;
    }
    
    // 使用平均差异来判断变化，而不是瞬时差异
    bool hasChanged = averageDiff > (pixelDeltaThreshold * tileSize * tileSize * 3);
    
    if (!inProtection)
    {
        if (hasChanged)
        {
            // 区域发生变化
            if (stableCounter == -1) // 从未变化过的区域
            {
                stableCounter = (int)firstRefreshExtraDelay;
            }
            else if (stableCounter == -2) // 刚冷却完的区域
            {
                stableCounter = (int)firstRefreshExtraDelay;
            }
            else if (stableCounter > (int)stableFramesRequired && stableCounter <= (int)(stableFramesRequired + additionalCooldownFrames))
            {
                // 在冷却期内发生变化，忽略，不重置计数器
            }
            else
            {
                // 正在检测中的区域发生变化，重置为1
                stableCounter = 1;
            }
        }
        else if (stableCounter >= 0)
        {
            // 区域未变化，且正在检测中，增加稳定计数
            if (stableCounter < (int)(stableFramesRequired + additionalCooldownFrames))
            {
                stableCounter++;
            }
            else
            {
                // 冷却期结束，重置为-2
                stableCounter = -2;
            }
        }
        
        // 检查是否达到刷新条件
        if (stableCounter >= (int)stableFramesRequired && stableCounter < (int)(stableFramesRequired + additionalCooldownFrames))
        {
            // 添加到刷新列表
            uint writeIndex;
            g_refreshCounter.InterlockedAdd(0, 1, writeIndex);
            g_refreshList[writeIndex] = tileIdx;
            
            // 重置状态为“已刷新/冷却中”
            stableCounter = -2;
            // 设置保护期
            // 计算新的过期帧数
            uint newExpiryLow = currentFrameNumber + protectionFrames;
            uint newExpiryHigh = 0;
            
            // 检查是否溢出
            if (newExpiryLow < currentFrameNumber) // 发生溢出
            {
                newExpiryHigh = 1;
            }
            
            expiryFrame.x = newExpiryLow;
            expiryFrame.y = newExpiryHigh;
        }
    }
    
    // 5. 将更新后的状态写回缓冲区
    g_tileStableCounters[tileIdx] = stableCounter;
    g_tileProtectionExpiry[tileIdx] = expiryFrame;
}

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
    uint protectionFrames;
    uint boundingAreaWidth;
    uint boundingAreaHeight;
    uint boundingAreaHistoryFrames;
    uint boundingAreaChangeThreshold;
    uint boundingAreaRefreshBlockThreshold; // 新增：判定合围区域刷新所需的区块数阈值
    uint padding1;
}

Texture2D<float4> g_texPrev : register(t0);
Texture2D<float4> g_texCurr : register(t1);

RWStructuredBuffer<uint4> g_tileHistoryIn : register(u0);
RWStructuredBuffer<uint4> g_tileHistoryOut : register(u1);

RWStructuredBuffer<uint> g_refreshList : register(u2);
RWByteAddressBuffer g_refreshCounter : register(u3);

RWStructuredBuffer<float> g_tileBrightness : register(u4);

RWStructuredBuffer<int> g_tileStableCounters : register(u5);
RWStructuredBuffer<uint2> g_tileProtectionExpiry : register(u6);

StructuredBuffer<uint> g_boundingAreaHistory : register(t2);
RWStructuredBuffer<uint> g_boundingAreaTileChangeCount : register(u7);


[numthreads(8, 8, 1)]
void CSMain(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint tilesX = (screenW + tileSize - 1) / tileSize;
    if (dispatchThreadId.x >= tilesX || dispatchThreadId.y >= ((screenH + tileSize - 1) / tileSize))
        return;
        
    uint tileIdx = dispatchThreadId.y * tilesX + dispatchThreadId.x;

    uint currentFrameTileDiffSum = 0;
    float tileBrightnessSum = 0.0f;
    uint pixelCount = 0;
    
    for (uint y = 0; y < tileSize; ++y)
    {
        for (uint x = 0; x < tileSize; ++x)
        {
            uint2 pixelPos = dispatchThreadId.xy * tileSize + uint2(x, y);
            if (pixelPos.x < screenW && pixelPos.y < screenH)
            {
                float4 prevColor = g_texPrev[pixelPos];
                float4 currColor = g_texCurr[pixelPos];
                uint3 diff = (uint3)(abs(currColor.rgb - prevColor.rgb) * 255.0f);
                currentFrameTileDiffSum += diff.r + diff.g + diff.b;
                
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
    
    uint2 expiryFrameCheck = g_tileProtectionExpiry[tileIdx];
    bool inProtection = expiryFrameCheck.y > 0 || (expiryFrameCheck.y == 0 && expiryFrameCheck.x > currentFrameNumber);
    if (inProtection)
    {
        currentFrameTileDiffSum = 0;
    }

    uint4 historyIn = g_tileHistoryIn[tileIdx];
    uint4 historyOut;
    historyOut.xyz = historyIn.yzw;
    historyOut.w = currentFrameTileDiffSum;
    g_tileHistoryOut[tileIdx] = historyOut;

    uint sumForAverage = historyOut.w + historyIn.w + historyIn.z + historyIn.y + historyIn.x;
    uint averageDiff = sumForAverage / max(1, averageWindowSize);
    bool hasChanged = averageDiff > (pixelDeltaThreshold * tileSize * tileSize * 3);

    // Re-enable scrolling detection
    bool isScrollingContent = false;
    if (boundingAreaWidth > 0 && boundingAreaHeight > 0)
    {
        uint boundingAreasX = (tilesX + boundingAreaWidth - 1) / boundingAreaWidth;
        uint2 boundingAreaIndex = dispatchThreadId.xy / uint2(boundingAreaWidth, boundingAreaHeight);
        uint boundingAreaIdx = boundingAreaIndex.y * boundingAreasX + boundingAreaIndex.x;

        if (hasChanged)
        {
            InterlockedAdd(g_boundingAreaTileChangeCount[boundingAreaIdx], 1);
        }
        
        // 只有当变化区块数达到阈值时，才认为该合围区域正在滚动
        uint areaChangeCount = g_boundingAreaTileChangeCount[boundingAreaIdx];
        if (areaChangeCount >= boundingAreaRefreshBlockThreshold)
        {
            isScrollingContent = true;
        }
        else
        {
            // 保留原有的基于历史帧的判断逻辑作为备选
            uint historyData = g_boundingAreaHistory[boundingAreaIdx];
            uint significantChangeCount = 0;
            uint maxTests = boundingAreaHistoryFrames < 32 ? boundingAreaHistoryFrames : 32;
            for (uint i = 0; i < maxTests; ++i)
            {
                if ((historyData & (1u << i)) != 0)
                {
                    significantChangeCount++;
                }
            }
            
            if (significantChangeCount >= boundingAreaChangeThreshold)
            {
                isScrollingContent = true;
            }
        }
    }
    
    int stableCounter = g_tileStableCounters[tileIdx];
    if (hasChanged)
    {
        stableCounter = (stableCounter < 0) ? (int)firstRefreshExtraDelay : 1;
    }
    else if (stableCounter >= 0)
    {
        stableCounter = (stableCounter < (int)(stableFramesRequired + additionalCooldownFrames)) ? (stableCounter + 1) : -2;
    }
    
    if (!inProtection && !isScrollingContent && stableCounter >= (int)stableFramesRequired && stableCounter < (int)(stableFramesRequired + additionalCooldownFrames))
    {
        uint writeIndex;
        g_refreshCounter.InterlockedAdd(0, 1, writeIndex);
        g_refreshList[writeIndex] = tileIdx;
        
        stableCounter = -2;
        uint newExpiryLow = currentFrameNumber + protectionFrames;
        uint newExpiryHigh = (newExpiryLow < currentFrameNumber) ? 1 : 0;
        g_tileProtectionExpiry[tileIdx] = uint2(newExpiryLow, newExpiryHigh);
    }
    
    g_tileStableCounters[tileIdx] = stableCounter;
}
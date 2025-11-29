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
    uint2 boundingAreaSize;
    uint boundingAreaHistoryFrames;
    uint boundingAreaChangeThreshold;
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

// 合围区域历史帧变化数据 - 每个合围区域一个uint
RWStructuredBuffer<uint> g_boundingAreaHistory : register(u7);

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

    // --- 关键修改点 ---
    // 在更新历史记录之前，检查保护期
    uint2 expiryFrameCheck = g_tileProtectionExpiry[tileIdx];
    bool inProtection = false;
    if (expiryFrameCheck.y > 0 || (expiryFrameCheck.y == 0 && expiryFrameCheck.x > currentFrameNumber))
    {
        inProtection = true;
    }

    if (inProtection)
    {
        // 如果在保护期内，则强制认为本帧没有变化，避免污染历史记录
        currentFrameTileDiffSum = 0;
    }
    // --- 修改结束 ---

    uint4 historyIn = g_tileHistoryIn[tileIdx];
    uint4 historyOut;
    historyOut.xyz = historyIn.yzw;
    historyOut.w = currentFrameTileDiffSum;
    g_tileHistoryOut[tileIdx] = historyOut;

    uint sumForAverage = 0;
    if (averageWindowSize >= 1) sumForAverage += currentFrameTileDiffSum;
    if (averageWindowSize >= 2) sumForAverage += historyIn.w;
    if (averageWindowSize >= 3) sumForAverage += historyIn.z;
    if (averageWindowSize >= 4) sumForAverage += historyIn.y;
    if (averageWindowSize >= 5) sumForAverage += historyIn.x;

    uint effectiveAverageWindowSize = max(1, averageWindowSize);
    uint averageDiff = sumForAverage / effectiveAverageWindowSize;

    int stableCounter = g_tileStableCounters[tileIdx];
    uint2 expiryFrame = g_tileProtectionExpiry[tileIdx];
    
    bool hasChanged = averageDiff > (pixelDeltaThreshold * tileSize * tileSize * 3);
    
    // 计算当前区块属于哪个合围区域
    uint2 boundingAreaIndex;
    boundingAreaIndex.x = dispatchThreadId.x / boundingAreaSize.x;
    boundingAreaIndex.y = dispatchThreadId.y / boundingAreaSize.y;
    
    // 计算当前合围区域的起始和结束坐标
    uint2 boundingAreaStart, boundingAreaEnd;
    boundingAreaStart.x = boundingAreaIndex.x * boundingAreaSize.x;
    boundingAreaStart.y = boundingAreaIndex.y * boundingAreaSize.y;
    boundingAreaEnd.x = boundingAreaStart.x + boundingAreaSize.x;
    boundingAreaEnd.y = boundingAreaStart.y + boundingAreaSize.y;
    
    // 检查当前区块是否在合围区域内
    bool isInBoundingArea = (dispatchThreadId.x >= boundingAreaStart.x) && 
                         (dispatchThreadId.x < boundingAreaEnd.x) &&
                         (dispatchThreadId.y >= boundingAreaStart.y) && 
                         (dispatchThreadId.y < boundingAreaEnd.y);
    
    bool isScrollingContent = false;
    if (isInBoundingArea && boundingAreaHistoryFrames > 0 && boundingAreaChangeThreshold > 0) 
    {
        // 计算当前合围区域的索引
        uint boundingAreaIdx = boundingAreaIndex.y * ((screenW + boundingAreaSize.x - 1) / boundingAreaSize.x) + boundingAreaIndex.x;
        
        uint frameBit = hasChanged ? 1u : 0u;
        uint historyIndex = currentFrameNumber % boundingAreaHistoryFrames;
        uint bitPosition = historyIndex;
        
        uint historyData = g_boundingAreaHistory[boundingAreaIdx];
        
        uint mask = 1u << bitPosition;
        if (frameBit == 1)
            historyData |= mask;
        else
            historyData &= ~mask;
            
        g_boundingAreaHistory[boundingAreaIdx] = historyData;
        
        uint changeCount = 0;
        for (uint i = 0; i < min(boundingAreaHistoryFrames, currentFrameNumber + 1); ++i)
        {
            uint checkBitPosition = ((currentFrameNumber - i) % boundingAreaHistoryFrames);
            uint checkMask = 1u << checkBitPosition;
            if ((historyData & checkMask) != 0)
                changeCount++;
        }
        
        if (changeCount >= boundingAreaChangeThreshold)
            isScrollingContent = true;
    }
    
    if (hasChanged)
    {
        if (stableCounter == -1)
        {
            stableCounter = (int)firstRefreshExtraDelay;
        }
        else if (stableCounter == -2)
        {
            stableCounter = (int)firstRefreshExtraDelay;
        }
        else if (stableCounter > (int)stableFramesRequired && stableCounter <= (int)(stableFramesRequired + additionalCooldownFrames))
        {
        }
        else
        {
            stableCounter = 1;
        }
    }
    else if (stableCounter >= 0)
    {
        if (stableCounter < (int)(stableFramesRequired + additionalCooldownFrames))
        {
            stableCounter++;
        }
        else
        {
            stableCounter = -2;
        }
    }
    
    if (!inProtection && !isScrollingContent && stableCounter >= (int)stableFramesRequired && stableCounter < (int)(stableFramesRequired + additionalCooldownFrames))
    {
        uint writeIndex;
        g_refreshCounter.InterlockedAdd(0, 1, writeIndex);
        g_refreshList[writeIndex] = tileIdx;
        
        stableCounter = -2;
        uint newExpiryLow = currentFrameNumber + protectionFrames;
        uint newExpiryHigh = 0;
        
        if (newExpiryLow < currentFrameNumber)
        {
            newExpiryHigh = 1;
        }
        
        expiryFrame.x = newExpiryLow;
        expiryFrame.y = newExpiryHigh;
    }
    
    g_tileStableCounters[tileIdx] = stableCounter;
    g_tileProtectionExpiry[tileIdx] = expiryFrame;
}
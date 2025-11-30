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
    uint boundingAreaRefreshBlockThreshold; // New: threshold for determining refresh of bounding areas
    uint padding1;
}

Texture2D<float4> g_texPrev : register(t0);
Texture2D<float4> g_texCurr : register(t1);

// Use array-based buffers to support larger average windows
RWStructuredBuffer<uint> g_tileHistoryIn : register(u0);
RWStructuredBuffer<uint> g_tileHistoryOut : register(u1);

RWStructuredBuffer<uint> g_refreshList : register(u2);
RWByteAddressBuffer g_refreshCounter : register(u3);

RWStructuredBuffer<float> g_tileBrightness : register(u4);

RWStructuredBuffer<int> g_tileStableCounters : register(u5);
RWStructuredBuffer<uint2> g_tileProtectionExpiry : register(u6);

StructuredBuffer<uint> g_boundingAreaHistory : register(t2);
RWStructuredBuffer<uint> g_boundingAreaTileChangeCount : register(u7);

// Increase history frame count to 20
#define HISTORY_FRAME_COUNT 20

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
                
                // Calculate brightness (for OverlayForm)
                float luminance = 0.299f * currColor.r + 0.587f * currColor.g + 0.114f * currColor.b;
                tileBrightnessSum += luminance;
                pixelCount++;
            }
        }
    }
    
    // Write average brightness
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

    // Handle history with array-based approach
    // Read 20 history values (support up to 20-frame average)
    uint historyValues[HISTORY_FRAME_COUNT];
    for (int i = 0; i < HISTORY_FRAME_COUNT; i++) {
        historyValues[i] = g_tileHistoryIn[tileIdx * HISTORY_FRAME_COUNT + i];
    }
    
    // Shift history and add current value
    for (int i = 0; i < HISTORY_FRAME_COUNT - 1; i++) {
        g_tileHistoryOut[tileIdx * HISTORY_FRAME_COUNT + i] = historyValues[i + 1];
    }
    g_tileHistoryOut[tileIdx * HISTORY_FRAME_COUNT + HISTORY_FRAME_COUNT - 1] = currentFrameTileDiffSum;

    // Calculate sum based on averageWindowSize parameter
    uint sumForAverage = currentFrameTileDiffSum;
    for (uint i = 1; i < min(HISTORY_FRAME_COUNT, averageWindowSize); i++) {
        sumForAverage += historyValues[HISTORY_FRAME_COUNT - i];
    }
    
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
        
        // Only consider the bounding area as scrolling when the number of changed blocks reaches the threshold
        uint areaChangeCount = g_boundingAreaTileChangeCount[boundingAreaIdx];
        if (areaChangeCount >= boundingAreaRefreshBlockThreshold)
        {
            isScrollingContent = true;
        }
        else
        {
            // Retain the original judgment logic based on historical frames as an alternative
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
        // Modification: For -1 state blocks, do not set to firstRefreshExtraDelay, instead set to -100-firstRefreshExtraDelay
        if (stableCounter == -1) 
        {
            stableCounter = -100 - (int)firstRefreshExtraDelay;
        }
        else if (stableCounter < -100)
        {
           stableCounter ++;
        }
        else if (stableCounter == -2 && inProtection)
        {
            // Keep stability counter unchanged
        }
        else
        {
            stableCounter = 0;
        }
    }
    else if (stableCounter >= 0)
    {
        stableCounter = (stableCounter < (int)(stableFramesRequired + additionalCooldownFrames)) ? (stableCounter + 1) : -2;
    }
    else if (stableCounter < -100)
    {
        // Process -100 type counters, increment until reaching -100
        stableCounter++;

    }
    else if (stableCounter == -100)
    {
        stableCounter = -2;
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
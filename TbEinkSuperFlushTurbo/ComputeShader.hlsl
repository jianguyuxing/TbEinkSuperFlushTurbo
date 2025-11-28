cbuffer Params : register(b0)
{
    uint screenW;
    uint screenH;
    uint tileSize
    uint pixelDeltaThreshold;
    uint averageWindowSize;
    uint stableFramesRequired;
    uint additionalCooldownFrames;
    uint firstRefreshExtraDelay;
    uint currentFrameNumber;
}

Texture2D<float4> g_texPrev : register(t0);
Texture2D<float4> g_texCurr : register(t1);

RWStructuredBuffer<uint4> g_tileHistoryIn : register(u0);
RWStructuredBuffer<uint4> g_tileHistoryOut : register(u1);

RWStructuredBuffer<uint> g_refreshList : register(u2);
RWByteAddressBuffer g_refreshCounter : register(u3);

RWStructuredBuffer<float> g_tileBrightness : register(u4);

RWStructuredBuffer<int> g_tileStableCounters : register(u5);
RWStructuredBuffer<uint> g_tileProtectionExpiry : register(u6);

RWStructuredBuffer<uint> g_changedTilesCount : register(u7);
RWStructuredBuffer<uint> g_readyToRefreshCount : register(u8);

[numthreads(8, 8, 1)]
void main(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint tilesX = (screenW + tileSize - 1) / tileSize;
    uint tilesY = (screenH + tileSize - 1) / tileSize;
    
    if (dispatchThreadId.x >= tilesX || dispatchThreadId.y >= tilesY)
        return;
        
    uint tileIdx = dispatchThreadId.y * tilesX + dispatchThreadId.x;
    uint tileX = dispatchThreadId.x * tileSize;
    uint tileY = dispatchThreadId.y * tileSize;

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

                currentFrameTileDiffSum += (uint)(abs(prevColor.r - currColor.r) * 255.0f);
                currentFrameTileDiffSum += (uint)(abs(prevColor.g - currColor.g) * 255.0f);
                currentFrameTileDiffSum += (uint)(abs(prevColor.b - currColor.b) * 255.0f);
                
                float luminance = 0.299f * currColor.r + 0.587f * currColor.g + 0.114f * currColor.b;
                tileBrightnessSum += luminance;
                pixelCount++;
            }
        }
    }
    
    if (pixelCount > 0)
    {
        g_tileBrightness[tileIdx] = tileBrightnessSum / (float)pixelCount;
    }
    else
    {
        g_tileBrightness[tileIdx] = 0.0f;
    }

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
    uint protectionExpiry = g_tileProtectionExpiry[tileIdx];
    
    bool inProtection = protectionExpiry > currentFrameNumber;
    bool hasChanged = currentFrameTileDiffSum > (pixelDeltaThreshold * tileSize * tileSize * 3);
    
    uint changedCount = 0;
    uint readyCount = 0;
    
    if (!inProtection)
    {
        if (hasChanged)
        {
            changedCount = 1;
            
            if (stableCounter == -1)
            {
                stableCounter = firstRefreshExtraDelay;
            }
            else if (stableCounter == -2)
            {
                stableCounter = firstRefreshExtraDelay;
            }
            else if (stableCounter > stableFramesRequired && stableCounter <= stableFramesRequired + additionalCooldownFrames)
            {
            }
            else
            {
                stableCounter = 1;
            }
        }
        else if (stableCounter >= 0)
        {
            if (stableCounter < stableFramesRequired + additionalCooldownFrames)
            {
                stableCounter++;
            }
            else
            {
                stableCounter = -2;
            }
        }
        
        if (stableCounter >= 0 && stableCounter >= stableFramesRequired)
        {
            readyCount = 1;
            stableCounter = -2;
            protectionExpiry = currentFrameNumber + 30;
        }
    }
    
    if (changedCount > 0)
    {
        uint oldCount;
        InterlockedAdd(g_changedTilesCount[0], changedCount, oldCount);
    }
    
    if (readyCount > 0)
    {
        uint oldCount;
        InterlockedAdd(g_readyToRefreshCount[0], readyCount, oldCount);
        
        uint writeIndex;
        g_refreshCounter.InterlockedAdd(0, 1, writeIndex);
        g_refreshList[writeIndex] = tileIdx;
    }
    
    g_tileStableCounters[tileIdx] = stableCounter;
    g_tileProtectionExpiry[tileIdx] = protectionExpiry;
}
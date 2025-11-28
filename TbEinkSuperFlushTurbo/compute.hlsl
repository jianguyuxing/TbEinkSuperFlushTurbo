// compute.hlsl
// This shader calculates pixel differences per tile and manages per-tile history.

cbuffer Params : register(b0)
{
    uint screenW;
    uint screenH;
    uint tileSize;
    uint pixelDeltaThreshold; // Per-component threshold (e.g., 15)
    uint averageWindowSize;   // Total number of frames for average (e.g., 5). Max 5 for uint4.
}

Texture2D<float4> g_texPrev : register(t0);
Texture2D<float4> g_texCurr : register(t1);

// Each element (uint4) stores the last 4 difference sums for a tile.
// historyIn.x is oldest, historyIn.w is newest (from previous frame).
RWStructuredBuffer<uint4> g_tileHistoryIn : register(u0);
RWStructuredBuffer<uint4> g_tileHistoryOut : register(u1);

RWStructuredBuffer<uint> g_refreshList : register(u2);
// Use RWByteAddressBuffer for the atomic counter, matching the C# code (raw buffer view)
RWByteAddressBuffer g_refreshCounter : register(u3);

// 新增：瓦片亮度数据输出
RWStructuredBuffer<float> g_tileBrightness : register(u4);

[numthreads(1, 1, 1)] // Each thread group processes one tile
void CSMain(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint tilesX = (screenW + tileSize - 1) / tileSize;
    uint tileIdx = dispatchThreadId.y * tilesX + dispatchThreadId.x;

    uint tileX = dispatchThreadId.x * tileSize;
    uint tileY = dispatchThreadId.y * tileSize;

    uint currentFrameTileDiffSum = 0; // Sum of absolute differences for current tile (curr vs prev)

    // Calculate immediate difference for the current tile (current frame vs previous frame)
    float tileBrightnessSum = 0.0f; // 新增：用于计算瓦片平均亮度
    uint pixelCount = 0; // 新增：记录有效像素数量
    
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

                // Sum of absolute differences for R, G, B components, scaled to 0-255 range
                currentFrameTileDiffSum += (uint)(abs(prevColor.r - currColor.r) * 255.0f);
                currentFrameTileDiffSum += (uint)(abs(prevColor.g - currColor.g) * 255.0f);
                currentFrameTileDiffSum += (uint)(abs(prevColor.b - currColor.b) * 255.0f);
                
                // 新增：计算当前帧的亮度（使用标准亮度公式）
                float luminance = 0.299f * currColor.r + 0.587f * currColor.g + 0.114f * currColor.b;
                tileBrightnessSum += luminance;
                pixelCount++;
            }
        }
    }
    
    // 新增：计算并存储瓦片平均亮度
    if (pixelCount > 0)
    {
        g_tileBrightness[tileIdx] = tileBrightnessSum / (float)pixelCount;
    }
    else
    {
        g_tileBrightness[tileIdx] = 0.0f;
    }

    // Read historical differences for this tile from the input buffer
    uint4 historyIn = g_tileHistoryIn[tileIdx];

    // --- Corrected History Update Logic ---
    // Prepare new history for the output buffer by shifting old values left
    // and inserting the new value at the end.
    uint4 historyOut;
    historyOut.xyz = historyIn.yzw; // Shift old values (y -> x, z -> y, w -> z)
    historyOut.w = currentFrameTileDiffSum; // Insert current diff as the newest

    // Write updated history to output buffer
    g_tileHistoryOut[tileIdx] = historyOut;

    // Calculate average difference over `averageWindowSize` frames.
    uint sumForAverage = 0;
    if (averageWindowSize >= 1) sumForAverage += currentFrameTileDiffSum;
    if (averageWindowSize >= 2) sumForAverage += historyIn.w;
    if (averageWindowSize >= 3) sumForAverage += historyIn.z;
    if (averageWindowSize >= 4) sumForAverage += historyIn.y;
    if (averageWindowSize >= 5) sumForAverage += historyIn.x;

    // Ensure we don't divide by zero if averageWindowSize is 0 (though it should be at least 1)
    uint effectiveAverageWindowSize = max(1, averageWindowSize);
    uint averageDiff = sumForAverage / effectiveAverageWindowSize;

    // Scale pixelDeltaThreshold for total tile difference
    // pixelDeltaThreshold is per-component. For a tile, it's tileSize*tileSize pixels, each with 3 components.
    uint tileDiffThreshold = pixelDeltaThreshold * tileSize * tileSize * 3;

    bool shouldRefresh = false;

    // 简化检测：直接使用阈值，提高灵敏度
    if (currentFrameTileDiffSum > tileDiffThreshold)
    {
        shouldRefresh = true;
    }

    if (shouldRefresh)
    {
        // Atomically increment the counter and get the old value as the write index
        uint writeIndex;
        g_refreshCounter.InterlockedAdd(0, 1, writeIndex);
        g_refreshList[writeIndex] = tileIdx;
    }
}
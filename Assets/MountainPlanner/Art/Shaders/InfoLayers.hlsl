// Info layers (task 12b, owner 2026-10-02): the colours the Slope angle, Exposure and Snow depth info layers paint
// over the ground, and the contour lines that draw over anything. Shared by the terrain and the cliff shells so
// both read as one surface. MapLayers sets the globals; switching is a value, never a rebuild. The legend
// colours in MountainHud (InfoLegend) match the constants here.
#ifndef MP_INFO_LAYERS_INCLUDED
#define MP_INFO_LAYERS_INCLUDED

float _MP_InfoView;          // 0 none, 2 slope angle, 3 exposure, 4 snow depth (MapLayers.InfoView)
float _MP_Contours;          // 1: contour lines on
float _MP_GridConvergence;   // radians from grid north clockwise to true north
float _MP_ContourInterval;   // metres between contours: 40 ft or 10 m (UnitFormat.ContourIntervalMetres)
float _MP_SnowDepthStops[6]; // metres at each snow-depth colour: round numbers in the player's units

// Slope angle: steepness as a grade in percent (rise over run; 100% is 45°), in the trail-difficulty bands of
// SlopeBands (green, blue, black; double black is black with white hatching, as two diamonds are on a sign).
static const float SlopeBlue = 25, SlopeBlack = 40, SlopeDouble = 60;   // percent
static const float3 SlopeGreenColour = float3(0.106, 0.541, 0.298);   // #1B8A4C
static const float3 SlopeBlueColour = float3(0.110, 0.369, 0.753);    // #1C5EC0
static const float3 SlopeBlackColour = float3(0.165, 0.165, 0.170);   // #2A2A2B, charcoal so the relief still reads

// Exposure: the way a slope faces, eight compass points from true north, cool to the north and warm to the
// south. Flatter than 10% faces nowhere.
static const float ExposureFlatPercent = 10;
static const float3 ExposureFlatColour = float3(0.62, 0.62, 0.60);
static const float3 ExposureColours[8] =
{
    float3(0.231, 0.420, 0.820),   // N  #3B6BD1
    float3(0.180, 0.620, 0.690),   // NE #2E9EB0
    float3(0.345, 0.690, 0.400),   // E  #58B066
    float3(0.835, 0.745, 0.255),   // SE #D5BE41
    float3(0.910, 0.525, 0.220),   // S  #E88638
    float3(0.808, 0.325, 0.255),   // SW #CE5341
    float3(0.675, 0.310, 0.580),   // W  #AC4F94
    float3(0.420, 0.345, 0.780),   // NW #6B58C7
};

// Snow depth: bare ground, then pale to deep blue and violet, at _MP_SnowDepthStops.
static const float3 SnowDepthColours[6] =
{
    float3(0.545, 0.455, 0.345),   // bare       #8B7458
    float3(0.890, 0.925, 0.960),   // 15 cm      #E3ECF5
    float3(0.600, 0.765, 0.910),   // 50 cm      #99C3E8
    float3(0.310, 0.545, 0.835),   // 1 m        #4F8BD5
    float3(0.180, 0.290, 0.640),   // 2 m        #2E4AA3
    float3(0.290, 0.165, 0.470),   // 3 m and up #4A2A78
};

// Contours: every 40 ft (10 m), heavier every 200 ft (50 m), heavier still every 1,000 ft (250 m); a level fades
// out when its lines would sit closer than a few pixels, so zooming out leaves only the heavier ones.
static const float3 ContourColour = float3(0.22, 0.17, 0.12);

// pixel: the fragment's screen position (SV_POSITION.xy). The double-black hatching is drawn on the screen, 7 px
// apart, so it reads at every distance (hatching fixed to the ground faded out a kilometre away).
float3 SlopeAngleColour(float percent, float2 pixel)
{
    float3 colour = percent < SlopeBlue ? SlopeGreenColour : percent < SlopeBlack ? SlopeBlueColour : SlopeBlackColour;
    if (percent >= SlopeDouble)
    {
        float d = (pixel.x + pixel.y) / 7;
        float stripe = saturate(1.5 - abs(frac(d) - 0.5) * 7);   // about 2 px wide
        colour = lerp(colour, float3(0.92, 0.92, 0.92), stripe * 0.7);
    }
    return colour;
}

// downhill: the horizontal way down the slope in world x (east on the grid) and z (north on the grid).
float3 ExposureColour(float2 downhill, float percent)
{
    if (percent < ExposureFlatPercent || dot(downhill, downhill) < 1e-12) return ExposureFlatColour;
    float bearing = atan2(downhill.x, downhill.y) - _MP_GridConvergence;   // clockwise from true north
    int sector = (int)floor(frac(bearing / TWO_PI + 1.0 / 16) * 8) & 7;
    return ExposureColours[sector];
}

float3 SnowDepthColour(float metres)
{
    float3 colour = SnowDepthColours[5];
    [unroll] for (int k = 4; k >= 0; k--)
        if (metres < _MP_SnowDepthStops[k + 1])
            colour = lerp(SnowDepthColours[k], SnowDepthColours[k + 1],
                          saturate((metres - _MP_SnowDepthStops[k]) / max(_MP_SnowDepthStops[k + 1] - _MP_SnowDepthStops[k], 1e-3)));
    return colour;
}

// The info layer's colour at a point, lit by the sun and sky so the relief still reads ("the world dims
// slightly so the data stands out", game-ui-direction.md §4.5).
float3 ShadeInfo(float3 colour, float3 n, float3 sunDirection)
{
    return colour * (saturate(dot(n, sunDirection)) * 0.6 + 0.5);
}

// One level of contour lines: coverage 0-1 for lines every `interval` metres, `halfWidth` pixels each side.
float ContourLevel(float elevation, float interval, float halfWidth)
{
    float d = elevation / interval;
    float w = max(fwidth(d), 1e-6);
    float px = abs(frac(d + 0.5) - 0.5) / w;   // pixels to the nearest line
    float spacing = 1 / w;                     // pixels between lines
    return saturate(halfWidth + 0.5 - px) * saturate((spacing - 3) / 4);
}

float3 ApplyContours(float3 colour, float elevation)
{
    if (_MP_Contours < 0.5) return colour;
    float interval = _MP_ContourInterval > 0 ? _MP_ContourInterval : 10;
    float minor = ContourLevel(elevation, interval, 0.5) * 0.35;
    float index = ContourLevel(elevation, interval * 5, 0.8) * 0.6;
    float major = ContourLevel(elevation, interval * 25, 1.1) * 0.75;
    return lerp(colour, ContourColour, max(minor, max(index, major)));
}

#endif

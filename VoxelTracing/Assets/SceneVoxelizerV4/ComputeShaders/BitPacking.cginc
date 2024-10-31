#define BITS_32 32
#define BITS_24 24
#define BITS_20 20
#define BITS_18 18
#define BITS_16 16
#define BITS_15 15
#define BITS_12 12
#define BITS_11 11
#define BITS_10 10
#define BITS_9 9
#define BITS_8 8
#define BITS_7 7
#define BITS_6 6
#define BITS_5 5
#define BITS_4 4
#define BITS_3 3
#define BITS_2 2
#define BITS_1 1

#define BITS_32_MAX 2147483647
#define BITS_24_MAX 16777216
#define BITS_20_MAX 1048576
#define BITS_18_MAX 262144
#define BITS_16_MAX 65536
#define BITS_15_MAX 32767
#define BITS_12_MAX 4096
#define BITS_10_MAX 1024
#define BITS_9_MAX 512
#define BITS_8_MAX 256
#define BITS_7_MAX 128
#define BITS_6_MAX 64
#define BITS_5_MAX 32
#define BITS_4_MAX 16
#define BITS_3_MAX 8
#define BITS_2_MAX 4
#define BITS_1_MAX 2

#define BITS_32_MAX_VALUE BITS_32_MAX - 1
#define BITS_24_MAX_VALUE BITS_24_MAX - 1
#define BITS_20_MAX_VALUE BITS_20_MAX - 1
#define BITS_18_MAX_VALUE BITS_18_MAX - 1
#define BITS_16_MAX_VALUE BITS_16_MAX - 1
#define BITS_15_MAX_VALUE BITS_15_MAX - 1
#define BITS_12_MAX_VALUE BITS_12_MAX - 1
#define BITS_10_MAX_VALUE BITS_10_MAX - 1
#define BITS_9_MAX_VALUE BITS_9_MAX - 1
#define BITS_8_MAX_VALUE BITS_8_MAX - 1
#define BITS_7_MAX_VALUE BITS_7_MAX - 1
#define BITS_6_MAX_VALUE BITS_6_MAX - 1
#define BITS_5_MAX_VALUE BITS_5_MAX - 1
#define BITS_4_MAX_VALUE BITS_4_MAX - 1
#define BITS_3_MAX_VALUE BITS_3_MAX - 1
#define BITS_2_MAX_VALUE BITS_2_MAX - 1
#define BITS_1_MAX_VALUE BITS_1_MAX - 1

uint GetMaskOfBitsToKeep(uint bitOffsetStart, uint bitsToKeep)
{
    uint mask = (1u << bitsToKeep) - 1;
    return mask << bitOffsetStart;
}

uint KeepBitsOfValue(uint sourceData, uint bitsToKeepOffsetStart, uint amountOfBitsToKeep)
{
    return sourceData & GetMaskOfBitsToKeep(bitsToKeepOffsetStart, amountOfBitsToKeep);
}

uint ExtractBits(uint sourceData, uint bitOffsetStart, uint bitsToExtract)
{
    uint bitmask = (1u << bitsToExtract) - 1u;
    return (sourceData >> bitOffsetStart) & bitmask;
}

bool IsBitAtOffsetSet(uint sourceData, uint bitOffsetLocation)
{
    return ExtractBits(sourceData, bitOffsetLocation, 1u) != 0;
}

uint SetBitAtOffset(uint sourceData, uint bitOffsetLocation)
{
    return sourceData |= 1u << bitOffsetLocation;
}

uint ClearBitAtOffset(uint sourceData, uint bitOffsetLocation)
{
    return sourceData &= ~(1u << bitOffsetLocation);
}

uint CombineBits(uint sourceData, uint sourceDataBitSize, uint newData, uint newDataBitSize)
{
    uint bitsA = KeepBitsOfValue(sourceData, 0, sourceDataBitSize);
    uint bitsB = KeepBitsOfValue(newData, 0, newDataBitSize);
    return bitsA | bitsB << sourceDataBitSize;
}

//|||||||||||||||||||||||||||||||||||||| SRP CORE PACKING ||||||||||||||||||||||||||||||||||||||
//|||||||||||||||||||||||||||||||||||||| SRP CORE PACKING ||||||||||||||||||||||||||||||||||||||
//|||||||||||||||||||||||||||||||||||||| SRP CORE PACKING ||||||||||||||||||||||||||||||||||||||

// Unsigned integer bit field extraction.
// Note that the intrinsic itself generates a vector instruction.
// Wrap this function with WaveReadLaneFirst() to get scalar output.
uint BitFieldExtract(uint data, uint offset, uint numBits)
{
	uint mask = (1u << numBits) - 1u;
	return (data >> offset) & mask;
}

//-----------------------------------------------------------------------------
// Integer packing
//-----------------------------------------------------------------------------

// Packs an integer stored using at most 'numBits' into a [0..1] float.
float PackInt(uint i, uint numBits)
{
    uint maxInt = (1u << numBits) - 1u;
    return saturate(i * rcp(maxInt));
}

// Unpacks a [0..1] float into an integer of size 'numBits'.
uint UnpackInt(float f, uint numBits)
{
    uint maxInt = (1u << numBits) - 1u;
    return (uint)(f * maxInt + 0.5); // Round instead of truncating
}

// Packs a [0..255] integer into a [0..1] float.
float PackByte(uint i)
{
    return PackInt(i, 8);
}

// Unpacks a [0..1] float into a [0..255] integer.
uint UnpackByte(float f)
{
    return UnpackInt(f, 8);
}

// Packs a [0..65535] integer into a [0..1] float.
float PackShort(uint i)
{
    return PackInt(i, 16);
}

// Unpacks a [0..1] float into a [0..65535] integer.
uint UnpackShort(float f)
{
    return UnpackInt(f, 16);
}

// Packs 8 lowermost bits of a [0..65535] integer into a [0..1] float.
float PackShortLo(uint i)
{
    uint lo = BitFieldExtract(i, 0u, 8u);
    return PackInt(lo, 8);
}

// Packs 8 uppermost bits of a [0..65535] integer into a [0..1] float.
float PackShortHi(uint i)
{
    uint hi = BitFieldExtract(i, 8u, 8u);
    return PackInt(hi, 8);
}

float Pack2Byte(float2 inputs)
{
    float2 temp = inputs * float2(255.0, 255.0);
    temp.x *= 256.0;
    temp = round(temp);
    float combined = temp.x + temp.y;
    return combined * (1.0 / 65535.0);
}

float2 Unpack2Byte(float inputs)
{
    float temp = round(inputs * 65535.0);
    float ipart;
    float fpart = modf(temp / 256.0, ipart);
    float2 result = float2(ipart, round(256.0 * fpart));
    return result * (1.0 / float2(255.0, 255.0));
}

// Encode a float in [0..1] and an int in [0..maxi - 1] as a float [0..1] to be store in log2(precision) bit
// maxi must be a power of two and define the number of bit dedicated 0..1 to the int part (log2(maxi))
// Example: precision is 256.0, maxi is 2, i is [0..1] encode on 1 bit. f is [0..1] encode on 7 bit.
// Example: precision is 256.0, maxi is 4, i is [0..3] encode on 2 bit. f is [0..1] encode on 6 bit.
// Example: precision is 256.0, maxi is 8, i is [0..7] encode on 3 bit. f is [0..1] encode on 5 bit.
// ...
// Example: precision is 1024.0, maxi is 8, i is [0..7] encode on 3 bit. f is [0..1] encode on 7 bit.
//...
float PackFloatInt(float f, uint i, float maxi, float precision)
{
    // Constant
    float precisionMinusOne = precision - 1.0;
    float t1 = ((precision / maxi) - 1.0) / precisionMinusOne;
    float t2 = (precision / maxi) / precisionMinusOne;

    return t1 * f + t2 * float(i);
}

void UnpackFloatInt(float val, float maxi, float precision, out float f, out uint i)
{
    // Constant
    float precisionMinusOne = precision - 1.0;
    float t1 = ((precision / maxi) - 1.0) / precisionMinusOne;
    float t2 = (precision / maxi) / precisionMinusOne;

    // extract integer part
    i = int((val / t2) + rcp(precisionMinusOne)); // + rcp(precisionMinusOne) to deal with precision issue (can't use round() as val contain the floating number
    // Now that we have i, solve formula in PackFloatInt for f
    //f = (val - t2 * float(i)) / t1 => convert in mads form
    f = saturate((-t2 * float(i) + val) / t1); // Saturate in case of precision issue
}

// Define various variante for ease of read
float PackFloatInt8bit(float f, uint i, float maxi)
{
    return PackFloatInt(f, i, maxi, 256.0);
}

void UnpackFloatInt8bit(float val, float maxi, out float f, out uint i)
{
    UnpackFloatInt(val, maxi, 256.0, f, i);
}

float PackFloatInt10bit(float f, uint i, float maxi)
{
    return PackFloatInt(f, i, maxi, 1024.0);
}

void UnpackFloatInt10bit(float val, float maxi, out float f, out uint i)
{
    UnpackFloatInt(val, maxi, 1024.0, f, i);
}

float PackFloatInt16bit(float f, uint i, float maxi)
{
    return PackFloatInt(f, i, maxi, 65536.0);
}

void UnpackFloatInt16bit(float val, float maxi, out float f, out uint i)
{
    UnpackFloatInt(val, maxi, 65536.0, f, i);
}

//-----------------------------------------------------------------------------
// Float packing
//-----------------------------------------------------------------------------

// src must be between 0.0 and 1.0
uint PackFloatToUInt(float src, uint offset, uint numBits)
{
    return UnpackInt(src, numBits) << offset;
}

float UnpackUIntToFloat(uint src, uint offset, uint numBits)
{
    uint maxInt = (1u << numBits) - 1u;
    return float(BitFieldExtract(src, offset, numBits)) * rcp(maxInt);
}

uint PackToR10G10B10A2(float4 rgba)
{
    return (PackFloatToUInt(rgba.x, 0, 10) |
        PackFloatToUInt(rgba.y, 10, 10) |
        PackFloatToUInt(rgba.z, 20, 10) |
        PackFloatToUInt(rgba.w, 30, 2));
}

float4 UnpackFromR10G10B10A2(uint rgba)
{
    float4 output;
    output.x = UnpackUIntToFloat(rgba, 0, 10);
    output.y = UnpackUIntToFloat(rgba, 10, 10);
    output.z = UnpackUIntToFloat(rgba, 20, 10);
    output.w = UnpackUIntToFloat(rgba, 30, 2);
    return output;
}

// Both the input and the output are in the [0, 1] range.
float2 PackFloatToR8G8(float f)
{
    uint i = UnpackShort(f);
    return float2(PackShortLo(i), PackShortHi(i));
}

// Both the input and the output are in the [0, 1] range.
float UnpackFloatFromR8G8(float2 f)
{
    uint lo = UnpackByte(f.x);
    uint hi = UnpackByte(f.y);
    uint cb = (hi << 8) + lo;
    return PackShort(cb);
}

// Pack float2 (each of 12 bit) in 888
float3 PackFloat2To888(float2 f)
{
    uint2 i = (uint2)(f * 4095.5);
    uint2 hi = i >> 8;
    uint2 lo = i & 255;
    // 8 bit in lo, 4 bit in hi
    uint3 cb = uint3(lo, hi.x | (hi.y << 4));

    return cb / 255.0;
}

// Unpack 2 float of 12bit packed into a 888
float2 Unpack888ToFloat2(float3 x)
{
    uint3 i = (uint3)(x * 255.5); // +0.5 to fix precision error on iOS 
    // 8 bit in lo, 4 bit in hi
    uint hi = i.z >> 4;
    uint lo = i.z & 15;
    uint2 cb = i.xy | uint2(lo << 8, hi << 8);

    return cb / 4095.0;
}

// Pack 2 float values from the [0, 1] range, to an 8 bits float from the [0, 1] range
float PackFloat2To8(float2 f)
{
    float x_expanded = f.x * 15.0;                        // f.x encoded over 4 bits, can have 2^4 = 16 distinct values mapped to [0, 1, ..., 15]
    float y_expanded = f.y * 15.0;                        // f.y encoded over 4 bits, can have 2^4 = 16 distinct values mapped to [0, 1, ..., 15]
    float x_y_expanded = x_expanded * 16.0 + y_expanded;  // f.x encoded over higher bits, f.y encoded over the lower bits - x_y values in range [0, 1, ..., 255]
    return x_y_expanded / 255.0;

    // above 4 lines equivalent to:
    //return (16.0 * f.x + f.y) / 17.0; 
}

// Unpack 2 float values from the [0, 1] range, packed in an 8 bits float from the [0, 1] range
float2 Unpack8ToFloat2(float f)
{
    float x_y_expanded = 255.0 * f;
    float x_expanded = floor(x_y_expanded / 16.0);
    float y_expanded = x_y_expanded - 16.0 * x_expanded;
    float x = x_expanded / 15.0;
    float y = y_expanded / 15.0;
    return float2(x, y);
}

//
// Hue, Saturation, Value
// Ranges:
//  Hue [0.0, 1.0]
//  Sat [0.0, 1.0]
//  Lum [0.0, HALF_MAX]
//
#define EPSILON 1.0e-4
#define Epsilon 1e-10

//https://www.chilliant.com/rgb2hsv.html
float3 RgbToHsv(float3 c)
{
    float4 K = float4(0.0, -1.0 / 3.0, 2.0 / 3.0, -1.0);
    float4 p = lerp(float4(c.bg, K.wz), float4(c.gb, K.xy), step(c.b, c.g));
    float4 q = lerp(float4(p.xyw, c.r), float4(c.r, p.yzx), step(p.x, c.r));
    float d = q.x - min(q.w, q.y);

    float hue = abs(q.z + (q.w - q.y) / (6.0 * d + EPSILON));
    float saturation = d / (q.x + EPSILON);
    float value = q.x;

    return float3(hue, saturation, value);
}

float3 HsvToRgb(float3 c)
{
    float4 K = float4(1.0, 2.0 / 3.0, 1.0 / 3.0, 3.0);
    float3 p = abs(frac(c.xxx + K.xyz) * 6.0 - K.www);
    return c.z * lerp(K.xxx, saturate(p - K.xxx), c.y);
}

// Convert rgb to luminance
// with rgb in linear space with sRGB primaries and D65 white point
float Luminance(float3 linearRgb)
{
    return dot(linearRgb, float3(0.2126729, 0.7151522, 0.0721750));
}

#define RGBM_Range 6.0

float4 EncodeRGBM(float3 rgb)
{
    float maxRGB = max(rgb.x, max(rgb.g, rgb.b));
    float M = maxRGB / RGBM_Range;
    M = ceil(M * 255.0) / 255.0;
    return float4(rgb / (M * RGBM_Range), M);
}

float3 DecodeRGBM(float4 rgbm)
{
    return rgbm.rgb * (rgbm.a * RGBM_Range);
}

// Ref: http://floattimecollisiondetection.net/blog/?p=15
float4 PackToLogLuv(float3 vRGB)
{
	// M matrix, for encoding
	const float3x3 M = float3x3(0.2209, 0.3390, 0.4184, 0.1138, 0.6780, 0.7319, 0.0102, 0.1130, 0.2969);

	float4 vResult;
	float3 Xp_Y_XYZp = mul(vRGB, M);

	Xp_Y_XYZp = max(Xp_Y_XYZp, float3(1e-6, 1e-6, 1e-6));
	vResult.xy = Xp_Y_XYZp.xy / Xp_Y_XYZp.z;

	float Le = 2.0 * log2(Xp_Y_XYZp.y) + 127.0;

	vResult.w = frac(Le);
	vResult.z = (Le - (floor(vResult.w * 255.0)) / 255.0) / 255.0;

	return vResult;
}

float3 UnpackFromLogLuv(float4 vLogLuv)
{
	// Inverse M matrix, for decoding
	const float3x3 InverseM = float3x3(6.0014, -2.7008, -1.7996, -1.3320, 3.1029, -5.7721, 0.3008, -1.0882, 5.6268);

	float Le = vLogLuv.z * 255.0 + vLogLuv.w;
	float3 Xp_Y_XYZp;

	Xp_Y_XYZp.y = exp2((Le - 127.0) / 2.0);
	Xp_Y_XYZp.z = Xp_Y_XYZp.y / vLogLuv.y;
	Xp_Y_XYZp.x = vLogLuv.x * Xp_Y_XYZp.z;

	float3 vRGB = mul(Xp_Y_XYZp, InverseM);

	return max(vRGB, float3(0.0, 0.0, 0.0));
}

//https://aras-p.info/texts/CompactNormalStorage.html
//Method #1: Store X & Y and Reconstruct Z
/*
float2 EncodeNormal(half3 normal)
{
    return normal.xy * 0.5 + 0.5;
}

float3 DecodeNormal(half2 encodedNormal)
{
    half3 normal;
    normal.xy = encodedNormal * 2 - 1;
    normal.z = sqrt(1 - dot(normal.xy, normal.xy));
    return normal;
}
*/

//https://aras-p.info/texts/CompactNormalStorage.html
//Method #3: Spherical Coordinates
/*
#define kPI 3.1415926536f
float2 EncodeNormal(float3 normal)
{
    return (float2(atan2(normal.y, normal.x) / kPI, normal.z) + 1.0) * 0.5;
}

float3 DecodeNormal(float2 encodedNormal)
{
    float2 ang = encodedNormal * 2 - 1;
    float2 scth;
    sincos(ang.x * kPI, scth.x, scth.y);
    float2 scphi = float2(sqrt(1.0 - ang.y * ang.y), ang.y);
    return float3(scth.y * scphi.x, scth.x * scphi.x, scphi.y);
}
*/

//https://aras-p.info/texts/CompactNormalStorage.html
//Method #4: Spheremap Transform (Used in Cry Engine 3)
/*
float2 EncodeNormal(float3 normal)
{
    float2 enc = normalize(normal.xy) * (sqrt(-normal.z * 0.5 + 0.5));
    enc = enc * 0.5 + 0.5;
    return enc;
}

float3 DecodeNormal(float2 encodedNormal)
{
    float4 nn = float4(encodedNormal.xy, 0, 0) * float4(2, 2, 0, 0) + float4(-1, -1, 1, -1);
    float l = dot(nn.xyz, -nn.xyw);
    nn.z = l;
    nn.xy *= sqrt(l);
    return nn.xyz * 2 + float3(0, 0, -1);
}
*/

//https://aras-p.info/texts/CompactNormalStorage.html
//Method #4: Spheremap Transform (Lambert Azimuthal Equal-Area projection)
/*
float2 EncodeNormal(float3 normal)
{
    float f = sqrt(8 * normal.z + 8);
    return normal.xy / f + 0.5;
}

float3 DecodeNormal(float2 encodedNormal)
{
    float2 fenc = encodedNormal * 4 - 2;
    float f = dot(fenc, fenc);
    float g = sqrt(1 - f / 4);
    float3 n;
    n.xy = fenc * g;
    n.z = 1 - f / 2;
    return n;
}
*/

//https://aras-p.info/texts/CompactNormalStorage.html
//Method #4: Spheremap Transform
/*
float2 EncodeNormal(float3 normal)
{
    half p = sqrt(normal.z * 8 + 8);
    return normal.xy / p + 0.5;
}

float3 DecodeNormal(float2 encodedNormal)
{
    float2 fenc = encodedNormal * 4 - 2;
    float f = dot(fenc, fenc);
    float g = sqrt(1 - f / 4);
    float3 n;
    n.xy = fenc * g;
    n.z = 1 - f / 2;
    return n;
}
*/

//======================= NOTE TO SELF: BEST ONE SO FAR =======================
//https://aras-p.info/texts/CompactNormalStorage.html
//Method #7: Stereographic Projection
/*
float2 EncodeNormal(float3 normal)
{
    float scale = 1.7777;
    float2 enc = normal.xy / (normal.z + 1);
    enc /= scale;
    enc = enc * 0.5 + 0.5;
    return enc;
}

float3 DecodeNormal(float2 encodedNormal)
{
    half scale = 1.7777;
    half3 nn = float3(encodedNormal.xy, 0) * half3(2 * scale, 2 * scale, 0) + half3(-scale, -scale, 1);
    half g = 2.0 / dot(nn.xyz, nn.xyz);
    half3 n;
    n.xy = g * nn.xy;
    n.z = g - 1;
    return n;
}
*/

//https://knarkowicz.wordpress.com/2014/04/16/octahedron-normal-vector-encoding/
//Spherical coordinates
/*
#define kPI 3.14159265359f
#define kINV_PI 0.31830988618f
float2 EncodeNormal(float3 n)
{
    float2 f;
    f.x = atan2(n.y, n.x) * kINV_PI;
    f.y = n.z;
 
    f = f * 0.5 + 0.5;
    return f;
}
 
float3 DecodeNormal(float2 f)
{
    float2 ang = f * 2.0 - 1.0;
 
    float2 scth;
    sincos(ang.x * kPI, scth.x, scth.y);
    float2 scphi = float2(sqrt(1.0 - ang.y * ang.y), ang.y);
 
    float3 n;
    n.x = scth.y * scphi.x;
    n.y = scth.x * scphi.x;
    n.z = scphi.y;
    return n;
}
*/

//https://knarkowicz.wordpress.com/2014/04/16/octahedron-normal-vector-encoding/
//Octahedron-normal vectors
/*
float2 OctWrap(float2 v)
{
    return (1.0 - abs(v.yx)) * (v.xy >= 0.0 ? 1.0 : -1.0);
}
 
float2 EncodeNormal(float3 n)
{
    n /= (abs(n.x) + abs(n.y) + abs(n.z));
    n.xy = n.z >= 0.0 ? n.xy : OctWrap(n.xy);
    n.xy = n.xy * 0.5 + 0.5;
    return n.xy;
}
 
float3 DecodeNormal(float2 f)
{
    f = f * 2.0 - 1.0;
 
    // https://twitter.com/Stubbesaurus/status/937994790553227264
    float3 n = float3(f.x, f.y, 1.0 - abs(f.x) - abs(f.y));
    float t = saturate(-n.z);
    n.xy += n.xy >= 0.0 ? -t : t;
    return normalize(n);
}
*/
using System;
using System.Collections.Generic;
using UnityEngine;

// ── Profile p data structures (mirrors Python server response) ──────────────
// Deserialize JSON from /parse-intent endpoint

[Serializable]
public class AngleRangeData
{
    public string name;
    public float min_deg;
    public float max_deg;
    public float priority;
}

[Serializable]
public class FloatRangeData
{
    public float min;
    public float max;
}

[Serializable]
public class TargetInfoData
{
    public string type;
    public string label;
    public string count;
}

[Serializable]
public class CameraProfileData
{
    public string profile_id;
    public string effect_class;
    public string target_angle;
    public string target_shot_size;
    public List<AngleRangeData> angle_ranges;
    public FloatRangeData subject_scale_range;
    public string default_focus_anchor;
    public string notes;
}

[Serializable]
public class IntentOutputData
{
    public string warning;
    public string effect_class;
    public TargetInfoData target;
    public string view_preference;
    public string intensity;
    public CameraProfileData profile;
}

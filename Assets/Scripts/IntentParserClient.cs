using System;
using System.Collections;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;

public class IntentParserClient : MonoBehaviour
{
    [Header("Server")]
    [SerializeField] private string serverUrl = "http://127.0.0.1:8000/parse-intent";

    [Header("UI")]
    [SerializeField] private TMP_InputField commandInput;
    [SerializeField] private Button sendButton;
    [SerializeField] private TMP_Text resultText;

    [Header("OCP")]
    [SerializeField] private CameraCandidateGenerator candidateGenerator;

    private void Start()
    {
        if (sendButton != null)
            sendButton.onClick.AddListener(OnSendButtonClicked);
    }

    private void OnSendButtonClicked()
    {
        string command = commandInput != null ? commandInput.text : "";
        if (string.IsNullOrWhiteSpace(command))
        {
            SetResult("Please enter a command.");
            return;
        }
        StartCoroutine(SendIntentRequest(command));
    }

    private IEnumerator SendIntentRequest(string command)
    {
        SetResult("Analyzing...");

        string jsonBody = JsonUtility.ToJson(new IntentRequest { command = command });
        byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonBody);

        using (UnityWebRequest request = new UnityWebRequest(serverUrl, "POST"))
        {
            request.uploadHandler   = new UploadHandlerRaw(bodyRaw);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("Accept", "application/json");

            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success)
            {
                SetResult("Request failed:\n" + request.error);
                yield break;
            }

            string responseJson = request.downloadHandler.text;
            Debug.Log("[IntentParserClient] Response: " + responseJson);

            IntentOutputData output;
            try
            {
                output = JsonUtility.FromJson<IntentOutputData>(responseJson);
            }
            catch (Exception e)
            {
                SetResult("JSON parse error:\n" + e.Message);
                yield break;
            }

            ShowParsedResult(output);
        }
    }

    private void ShowParsedResult(IntentOutputData output)
    {
        // UI 표시
        string intensityText = string.IsNullOrEmpty(output.intensity)
            ? "unknown"
            : output.intensity;

        string message =
            "Effect: "    + output.effect_class + "\n" +
            "Intensity: " + intensityText + "\n" +
            "Target: "    + output.target.label + " / " + output.target.type + "\n" +
            "View: "      + output.view_preference + "\n" +
            "Shot: "      + output.profile.target_shot_size + "\n" +
            "Angle: "     + output.profile.target_angle + "\n" +
            (string.IsNullOrEmpty(output.warning) ? "" : "Warning: " + output.warning);

        SetResult(message);

        // OCP 실행 — profile p → 카메라 후보 생성
        if (candidateGenerator != null)
        {
            candidateGenerator.ApplyProfileAndGenerate(output);
        }
        else
        {
            Debug.LogWarning("[IntentParserClient] candidateGenerator is not assigned!");
        }
    }

    private void SetResult(string msg)
    {
        if (resultText != null) resultText.text = msg;
        else Debug.Log(msg);
    }
}

[Serializable]
public class IntentRequest
{
    public string command;
}

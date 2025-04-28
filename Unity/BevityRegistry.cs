using UnityEngine;
using System.Collections;

public class BevityRegistry : MonoBehaviour
{
    public string apiUrl = "localhost:5309";

    public string jsonResponse = "";


    public void FetchData() {
        StartCoroutine(PostJsonToApi());
    }

    private IEnumerator PostJsonToApi() {
        // Make sure the URL has the proper protocol
        string fullUrl = apiUrl;
        if (!fullUrl.StartsWith("http://") && !fullUrl.StartsWith("https://")) {
            fullUrl = "http://" + fullUrl;
        }

        // Create the JSON-RPC request body
        string jsonBody = @"{
        ""jsonrpc"": ""2.0"",
        ""method"": ""bevy/registry/schema"",
        ""id"": 0,
        ""params"": null
    }";

        // Create the POST request
        using (UnityEngine.Networking.UnityWebRequest request = new UnityEngine.Networking.UnityWebRequest(fullUrl, "POST")) {
            // Set up the request data
            byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(jsonBody);
            request.uploadHandler = new UnityEngine.Networking.UploadHandlerRaw(bodyRaw);
            request.downloadHandler = new UnityEngine.Networking.DownloadHandlerBuffer();

            // Set the content type header
            request.SetRequestHeader("Content-Type", "application/json");

            // Send the request
            yield return request.SendWebRequest();

            // Check for errors
            if (request.result == UnityEngine.Networking.UnityWebRequest.Result.ConnectionError ||
                request.result == UnityEngine.Networking.UnityWebRequest.Result.ProtocolError) {
                Debug.LogError($"Error: {request.error}");
                jsonResponse = $"Error: {request.error}";
            } else {
                // Store the response
                jsonResponse = request.downloadHandler.text;
                Debug.Log("JSON fetched successfully");
            }
        }
    }
}

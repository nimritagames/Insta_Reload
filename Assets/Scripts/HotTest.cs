using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class HotTest : MonoBehaviour
{
    void Start()
    {
        InvokeRepeating(nameof(TickTick), 0.25f, 1.0f);
    }

    private void TickTick()
    {
        Debug.Log("Hot Reload test method tick");
    }

    private void Update()
    {
        Log("Hello World!", 10);
    }

    void Log(string message, int num)
    {
        Debug.Log(message + " " + num);
    }
}

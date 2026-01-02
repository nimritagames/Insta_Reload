using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class HotTest : MonoBehaviour
{
    [SerializeField] int num1 = 5, num2 = 10;

    void Start()
    {
        InvokeRepeating(nameof(TickTick), 0.25f, 1.0f);
    }

    private void TickTick()
    {
        Debug.Log("Hot Reload test method tick");
        Add(num1, num2);
    }

    private void Update()
    {
        Log("Hello Hoomans! Its me Mario");
        Log("Hey! What are u doing.");
        Log(Add(num1, num2).ToString());
    }

    void Log(string message)
    {
        Debug.Log(message);
    }

    int Add(int a, int b)
    {
        return a + b;
    }
}

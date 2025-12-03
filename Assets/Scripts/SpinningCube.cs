using UnityEngine;

public class SpinningCube : MonoBehaviour
{
    [Tooltip("Controls whether the cube should spin")]
    public bool shouldSpin = true;
    
    [Tooltip("Rotation speed in degrees per second")]
    public float rotationSpeed = 90f;
    
    void Update() {
        if (shouldSpin)
            transform.Rotate(Vector3.up, rotationSpeed * Time.deltaTime, Space.World);
    }
}

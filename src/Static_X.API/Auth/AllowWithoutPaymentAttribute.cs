namespace Static_X.API.Auth;

// Only concrete actions can opt out; controller-wide exemptions are deliberately forbidden.
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class AllowWithoutPaymentAttribute : Attribute { }

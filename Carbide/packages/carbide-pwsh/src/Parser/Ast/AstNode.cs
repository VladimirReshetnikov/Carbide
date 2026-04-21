using CarbidePwsh.Errors;

namespace CarbidePwsh.Parser.Ast;

public abstract record AstNode(SourceLocation Location);

public abstract record StatementAst(SourceLocation Location) : AstNode(Location);

public abstract record ExpressionAst(SourceLocation Location) : AstNode(Location);

public enum BinaryOp
{
    Add, Subtract, Multiply, Divide, Modulo,
    Equal, NotEqual, LessThan, LessOrEqual, GreaterThan, GreaterOrEqual,
    CEqual, CNotEqual, CLessThan, CLessOrEqual, CGreaterThan, CGreaterOrEqual,
    IEqual, INotEqual, ILessThan, ILessOrEqual, IGreaterThan, IGreaterOrEqual,
    And, Or, Xor,
    BAnd, BOr, BXor,
    Is, IsNot, As,
}

public enum UnaryOp
{
    Plus, Negate, Not, BNot,
}

public enum AssignmentOp
{
    Assign, AddAssign, SubtractAssign, MultiplyAssign, DivideAssign, ModuloAssign,
}

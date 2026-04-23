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
    BAnd, BOr, BXor, Shl, Shr,
    Is, IsNot, As,

    // Phase 3 operators.
    Match, IMatch, CMatch, NotMatch, INotMatch, CNotMatch,
    Replace, IReplace, CReplace,
    Like, ILike, CLike, NotLike, INotLike, CNotLike,
    Contains, ICContains, CContains, NotContains, INotContains, CNotContains,
    In, NotIn, CIn, CNotIn, IIn, INotIn,
    Coalesce,
    Format, Join, Split,
}

public enum UnaryOp
{
    Plus, Negate, Not, BNot,
    Join, Split,
    PreIncrement, PreDecrement, PostIncrement, PostDecrement,
}

public enum AssignmentOp
{
    Assign, AddAssign, SubtractAssign, MultiplyAssign, DivideAssign, ModuloAssign,
}

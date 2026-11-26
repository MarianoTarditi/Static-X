import type { ComponentProps } from 'react';
import { Slot } from '@radix-ui/react-slot';
import { cva, type VariantProps } from 'class-variance-authority';
import { cn } from '@/lib/utils';

const buttonVariants = cva(
  'inline-flex items-center justify-center gap-2 whitespace-nowrap rounded-md text-sm font-semibold transition-colors focus-visible:outline-2 focus-visible:outline-offset-4 focus-visible:outline-ring disabled:pointer-events-none disabled:opacity-45 min-h-11 px-4 py-2',
  { variants: { variant: {
    default: 'bg-primary text-primary-foreground hover:bg-primary/85',
    outline: 'border border-border bg-background text-foreground hover:bg-muted',
    ghost: 'text-muted-foreground hover:bg-muted hover:text-foreground',
  } }, defaultVariants: { variant: 'default' } },
);

export function Button({ className, variant, asChild = false, type = 'button', ...props }:
  ComponentProps<'button'> & VariantProps<typeof buttonVariants> & { asChild?: boolean }) {
  const Comp = asChild ? Slot : 'button';
  return <Comp {...(!asChild ? { type } : {})} className={cn(buttonVariants({ variant, className }))} {...props} />;
}
